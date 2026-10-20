# Incident C: where the fix belongs

**The incident.** A user has an order open. They run "Cancel line", which cancels a line (the child) and recalculates the order's Total (the parent) in one save. That save gives the order a new `LastSaveDate`. The form still holds the stamp it loaded, so the user's next edit gets a 409, although nobody else touched the order.

**How conflict detection works today** (ShiftEntity, read from the source). The PUT handler loads the row, compares its `LastSaveDate` with the one the form sends, and answers 409 if they differ. It has these weaknesses:

- A save that changes nothing still moves the stamp: `UpsertAsync` stamps the root on every PUT.
- A save that changes a child *and* its parent moves the parent's stamp: the repository's save sweep stamps every modified row, the parent included. That is exactly this incident.
- Only the root's stamp is compared. A child's stamp is never checked, so an older child sent with an up-to-date root goes through.
- The check and the save are separate steps, and the UPDATE checks no version, so two saves that load the same version can both pass and both commit.

(`ConcurrencyDiagnosisTests`, on the Incident B branch, has one test per weakness.)

## Where the fix belongs

1. **Now (app layer):** an action that changes the order answers with the order as it now is, in the same shape as GET. `SalesOrderController.CancelLine` returns it, and `SalesOrderForm` gives it to the form through `@bind-Value`. The conflict check stays strict.
2. **Not by loosening the check:** the form's old copy really is out of date. Accepting its stamp would send the line back as not cancelled and undo the action, because child stamps are never compared and the lines are replaced on save.
3. **Long run (framework):** every app with actions on child records hits this trap, so the default belongs in the framework. The framework already does it in miniature: the attention-clear endpoint answers `ClearAttentionResponse { LastSaveDate }`, and `ShiftEntityForm` patches its stamp (and its cancel-restore copy) from it. Generalising that to every action, with a helper that returns the updated root, would fix this for everyone. Patching only the stamp would not be enough here, though: the Total and the lines change too, so the whole order has to come back.
4. **Also framework:** stamp the root only when something in it really changed, and compare a version the database checks (a rowversion or EF concurrency token per aggregate). Then nested saves stop invalidating open forms, without hiding real conflicts.
5. **Why both:** the app fix is small, local and needed today. The cause is shared, so the default has to change where the stamping and the form live.

## How it's tested

- API (`SalesOrderStaleStampTests`, SQL Server): the trap test (a form that keeps its copy gets 409; passes before and after the fix, and checks that the stamp really moved). The fix test (a form that takes the action's answer can save its next edit; failed while the action answered 204). The guard (a real edit by someone else after the action still gets 409).
- Blazor (`SalesOrderFormTests`, bUnit with mocked HTTP): after "Cancel line" the form holds the action's answer without a second load, Edit then Cancel restores the current version, and the next save sends the new stamp. It fails if the page ignores the answer.
- Not tested: the real browser, and what happens if the action runs while the form is in Edit mode (the button is hidden then, on purpose).

**Correction (review, 2026-10-20):** the first version of this note said `ShiftEntityForm` has no public way to take a new version, so the page rebuilt the form with `@key` and loaded the order again. That was wrong. The form follows a new `Value` from its parent (`ShiftFormBasic.OnParametersSet`), and `EditItem` snapshots that value. The bUnit test showed the assignment alone was enough, and the rebuild was the redundant part. The page now uses the answer directly, with no second GET.
