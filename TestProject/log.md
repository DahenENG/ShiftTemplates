# Week 1 — Documentation Friction Log

## Day 1

### 1. Documentation site unavailable
The docs site returned HTTP Error 500.31 when I tried to open it.

Error:
`Failed to load ASP.NET Core runtime.`

Impact:
I could not use the documented Get Started pages directly.

### 2. Shift templates were not initially installed
Running the .NET template listing did not show the Shift templates.

Impact:
I had to clone the ShiftTemplates repository and install/build the template tooling locally.

### 3. Template source could not be restored directly
Attempting to restore the generated template source failed because the solution contained template preprocessing directives.

Error:
`MSB5007: Error parsing the project configuration section...`
`The entry "<!--#if addFunctions-->" is invalid.`

Impact:
I had to use the ShiftTemplates.Builder to generate the project instead of restoring the template source directly.

### 4. Template builder required a restore first
The template builder initially failed because project assets had not been generated.

Error:
`NETSDK1004: project.assets.json not found`

Impact:
I had to restore ShiftTemplates.csproj before running the builder.

### 5. SQL Server Express was expected but was not installed
The generated application initially tried to connect to:

`localhost\sqlexpress`

Error:
`Error Locating Server/Instance Specified.`

Impact:
The application could not start its database seeding process.

Workaround:
I used the available LocalDB instance:

`(localdb)\MSSQLLocalDB`

### 6. Cosmos DB emulator was expected locally
The generated application attempted to connect to:

`localhost:8081`

Error:
`No connection could be made because the target machine actively refused it.`

Impact:
Cosmos DB synchronization produced errors during startup.

Workaround:
I disabled Cosmos DB in development configuration for this local Week 1 exercise.


### 7. The shiftentity scaffold generated the entity files, list/form pages, and API controller, but it did not add the new entity to Test.Web\Shared\NavMenu.razor. The generated /VehicleList page worked when opened directly, but a navigation link had to be added manually.
### 8. Generated entity UI fields were not automatic
The original Your First Entity documentation implied that adding properties to the entity and DTOs was enough to use the generated CRUD UI. In practice, the generated Vehicle form had no input fields and the list showed only ID until explicit form controls and PropertyColumn entries were added to the generated Web pages.

### Why the Day 3 and Day 4 work never became PRs (note added 2026-10-20)
I wrote both in Week 1: the Get Started fixes for the friction above (commit c63ffea) and a CONTRIBUTING.md (d36d445). But I committed them to my local `master` of ShiftFrameworkDocumentation and never pushed or opened a PR. I only noticed on Day 6 and saved them on the local branch `week1-docs-updates`; it was never pushed. By Week 4 the same pages were being re-verified and rewritten for Flagship C, so the Day 3 fixes went into Docs #3 with that work instead of a separate PR: LocalDB (item 5) and Cosmos off (item 6) are in Installation / Run the generated sample, and the empty generated form (item 8) is Step 4 of Your first entity. Item 7 (the scaffold adds no menu link) is still not in the docs. CONTRIBUTING.md was rewritten from the Week 4 workflow and opened as Docs #4 (2026-10-20). Lesson: push a branch and open the PR the same day, even as a draft.

---

# Week 2 — How the framework works

## Day 6 — Mapping (2026-09-27)

Branch: `day6-vehicle-generated-mapper` (ShiftTemplates) — PR: https://github.com/ShiftSoftware/ShiftTemplates/pull/9

### Two surprises before the real work
1. **The task premise is out of date.** AutoMapper was removed from the framework on 2026-08-25; no StockPlusPlus entity uses it. The closest equivalent: move **Vehicle** off its fully hand-written mapping (four `override` methods in `VehicleRepository`).
2. **My local clones were ~3 weeks old.** They were checked out at release `2026.09.08.3` (detached HEAD), 44 commits behind ShiftTemplates master (ShiftIdentity 39, ShiftMapper 23, ShiftEntity 15). I first did Day 6 with the ShiftEntity source generator (`UseGeneratedMapper()`), but on master that generator **was deleted on 2026-09-20**: mapping is now done by **ShiftMapper**. The first version of PR #9 would not have compiled on master. I updated all 10 repos to master and redid the work (old version kept on local branch `day6-old-shiftentity-generator`). Also found my Week 1 docs commits were only on my local `master` of ShiftFrameworkDocumentation, never pushed: now saved on branch `week1-docs-updates`.

### What changed (on current master)
- `VehicleRepository`: the four hand-written overrides are gone. Nothing replaces them: ShiftMapper declares the four maps automatically from `ShiftRepository<DB, Vehicle, VehicleListDTO, VehicleDTO>`.
- `StockPlusPlusMapper` (the project's one mapper class): Vehicle's view map + `.ReverseMap()` write map that **ignores the four audit members** (see below).
- New `StockPlusPlus.Test/Tests/VehicleMappingTests.cs` (6 tests) through the real host's `VehicleRepository`. Written first and run against the hand-written code (6/6), then unchanged against ShiftMapper.

### Behaviour difference I found (and kept the old behaviour)
The framework's rules pack maps the audit members both ways, and on UPDATE the repository only re-stamps `LastSaveDate` / `LastSavedByUserID`, so a request body could rewrite `CreateDate` / `CreatedByUserID`. The hand-written Vehicle code never wrote them. A test proved it (a fake 1999 `CreateDate` got through, with the old generator AND with ShiftMapper). Fixed with `ForMember(..., opt => opt.Ignore())` in the write map. This is a known framework decision (Q7 / gap C-3 in `docs/plans/automapper-removal/`): the guard is each entity owner's job. `IsDeleted` is already protected by the repository.

### Experiment: members that nothing maps to
Temporarily added `string? Color` to `VehicleDTO` and `VehicleListDTO` (no `Vehicle.Color`), and `string? InternalNote` to the entity only. Reverted after. Full diagnostics captured with `-p:ErrorLog=...sarif` because "note" level ones do not print in a normal build.

| Member | ShiftMapper (current) | Old ShiftEntity generator (for comparison) |
|---|---|---|
| `VehicleDTO.Color` (view) | ⚠️ SM0001 warning | ⚠️ SHENGEN004 |
| `VehicleListDTO.Color` (list) | ⚠️ SM0001 warning | ⚠️ SHENGEN007 (with paste-ready fix) |
| `VehicleDTO.Color` sent on save | ❌ nothing — silently dropped | ❌ nothing |
| entity-only `InternalNote` on save | ℹ️ SM0006 note (with `.ReverseMap()`); ⚠️ SM0001 warning if the write map is declared on its own | ❌ nothing |
| entity-only `InternalNote` on copy | ✅ copied | ❌ silently NOT copied |
| my `CreateMap` replacing the automatic map | ℹ️ SM0047 note | n/a |

Everything is a warning or note: the build still succeeds.

### Per direction: what it catches / what it misses (ShiftMapper)
- **MapToView** (entity → detail DTO): catches DTO members with no source (SM0001).
- **MapToEntity** (DTO → entity = saving): tells you about entity columns nothing writes (SM0006 note / SM0001). **Misses** DTO members with no entity target: what the browser sends is silently dropped. Also writes audit fields unless you ignore them.
- **MapToList** (entity → list row, becomes SQL): catches unmapped list columns (SM0001); SM0030 flags conversions that cannot become SQL.
- **CopyEntity** (entity → entity, reload after save): copies every member, entity-only ones included (the old generator missed those).

### Checks
- `StockPlusPlus.API`, `StockPlusPlus.Web`: 0 errors.
- Full `StockPlusPlus.Test`: **249 / 251**. Baseline on untouched master was 243 / 245 with the same 2 failures: `TestPriority.One/Two` (test ordering), broken since xUnit 3 → 4 upgrade on 2026-09-21. Not related to this change.

### Friction / notes for mentor
- Clones started on an old release (above). Worth adding "`git switch master && git pull`" to onboarding.
- Database tests needed SQL Server Express (`localhost\sqlexpress`); only LocalDB was installed (Week 1, item 5). LocalDB is not enough: the identity test fixture's safety check only accepts `.\SQLEXPRESS` / `localhost` / `127.0.0.1`. Installed SQL Server 2025 Express (`winget install Microsoft.SQLServer.2025.Express`; the 2022 package's download link returns 404) and the Cosmos DB emulator (`winget install Microsoft.Azure.CosmosEmulator`, Week 1 item 6). The emulator does not start with Windows: start it before running tests.
- `TestPriority` fails on master since the xUnit 4 upgrade.
- `dotnet build StockPlusPlus.API --no-incremental` failed in ShiftBlazor (`CS0103 MimeTypes`) on the old release, also without my change (looks like a parallel-build race). Not re-checked on master.
- No CI runs on PRs to ShiftTemplates, so "green" means local build + tests.

## Day 7 — Permissions / TypeAuth (2026-09-27)

Branch: `day7-car-typeauth` (ShiftTemplates, from master; independent of Day 6)

### What I built
My Week 1 entity (Make / Model / Year) lived in the git-ignored `TestProject` and its controller used `base(null)` (no permission checks at all). StockPlusPlus already has a `Vehicle`, so I recreated it there as **Car**, following the `shiftentity` item template's 8-file layout:
- Entity `Car` (temporal), DTOs `CarDTO` / `CarListDTO`, `CarRepository` (automatic ShiftMapper mapping, no code), `DbSet<Car> Cars`, migration `AddCar` (creates only the `Cars` table + `CarsHistory`).
- **Action tree:** `StockPlusPlusActionTree.Car = new ReadWriteDeleteAction("Car")`.
- **Gating:** `CarController : ShiftEntitySecureControllerAsync<...>` with `base(StockPlusPlusActionTree.Car)`.
- UI: `CarList` / `CarForm` with `TypeAuthAction` (hides buttons), and a "Cars" menu link via `AuthorizedMudNavLink` (hidden without Car access). Shared files use `#if (includeSampleApp)` like the rest of the sample.

### How the gates work (read from `ShiftEntitySecureControllerAsync`)
| Request | Check |
|---|---|
| `GET api/Car` (list), `GET api/Car/{id}` (view) | `CanRead` |
| `POST api/Car` (insert), `PUT api/Car/{id}` (update) | `CanWrite` |
| `DELETE api/Car/{id}` | `CanDelete` |
Every check is `action is not null && !CanX(action)` → **`base(null)` silently switches all of them off.** That is why the Week 1 controller allowed everything.

### Tests (`CarPermissionTests`, 3 tests, real API + SQL Server)
Each test client carries only the access tree it is given (`factory.CreateAuthenticatedClient(accessTreeJson)`):
- Full (Read+Write+Delete): list, view, create, update, delete all succeed.
- **Read-only (second user):** list + view OK; create, update, delete → **403**; and afterwards the car is still there, unchanged and not deleted (the refusal really stopped it, not just the status code).
- No Car access: even list and view → 403.
- **Proof the tests catch the bug:** temporarily set the controller back to `base(null)` → the read-only user *created* a car (201) and the no-access user *read* the list (200); both tests failed. Restored.

### Things I learned on the way
- Creating returns **201 Created**, not 200.
- Without the `DataGridExport` permission, a list request may ask for at most **5 rows** (`$top=5`); more → 400 "Query Limit Exceeded".
- A hidden button is only UI: the API check in the controller is what protects the data.

### Checks
- API and Web: 0 errors. Full suite: **246 / 248** (baseline 243 + 3 new; same 2 `TestPriority` failures as master).
- `dotnet ef migrations has-pending-model-changes` was clean before adding `AddCar`. EF added a BOM and bumped `ProductVersion` to 10.0.12 (local EF tool) in the snapshot; BOM removed.

## Day 8 — Forms and a known legacy bug (2026-09-28)

Branch: `day8-legacy-form-side-by-side` (ShiftTemplates, from master). Mentor notes not received: investigated from the source.

### What the legacy pattern is
`@inherits ShiftForm<TPage, TDto>` makes `TheItem`, `Mode`, `Key` and `FormContainer` properties of the PAGE. `ShiftForm<,>` is `[Obsolete]` (ShiftBlazor 2eb3096, 2026-09-26, spells out the migration); the docs site migrated its CustomerForm demo off it on 2026-09-04 (7495a46).

### What I built (StockPlusPlus.Web/Pages/Samples/SideBySideForms/)
- `LegacySideBySideCountries.razor`: legacy way, two `ShiftEntityForm` panes both `@bind-Value="TheItem" @bind-Mode="Mode" @ref="FormContainer"`, one shared body reading `TheItem.Name`.
- `SideBySideCountries.razor`: fixed, each pane `@bind-Value` to its own field, shared body is a `RenderFragment<FormChildContext<CountryDTO>>` reading `context.Item.Name` / `context.ReadOnly`.
- `README.md`: the bug, why, the fix. `Pages/*/**` is excluded from the template, so none of this reaches new projects.

### What each legacy pane shows, and why (verified with bUnit, A = Iraq, B = Turkey)
- Both panes show **Turkey**. Left pane: `Key=A`, `Value.ID=B`. Page TheItem, left Value and right Value are the **same object**.
- Why: each form fetches by Key → `SetValue` → `ValueChanged` → `@bind-Value` writes the page's single `TheItem` → re-render pushes it into both forms. Last load wins.
- **Save corrupts data:** Save uses Key for the URL and Value for the body → `PUT Country/A` with `{ id: B, name: Turkey }`.
- **Edit in one pane puts both in edit mode** (shared `@bind-Mode`).
- **Second trap found:** `ShiftForm.SetParametersAsync` drops every cascading parameter, and `[SupplyParameterFromQuery]` is delivered as one → `?Left=...&Right=...` silently null, both panes opened empty in Create mode. Switched both pages to route keys.

### Tests (`StockPlusPlus.Web.Tests/SideBySideFormsTests.cs`, bUnit + MockHttp)
Legacy: same record in both panes; Edit flips both; Save PUTs the other record under its key. Fixed: each pane its own record; only one pane enters edit; Save PUTs its own record. Legacy tests pin the bug (they pass because it is there).

### Checks
- API, Web: 0 errors (one intended CS0618 warning on the legacy page). Web.Tests **24 / 24** (20 + 4). API tests 243 / 245 (baseline, same TestPriority failures).

### Things I learned
- bUnit: `[SupplyParameterFromQuery]` needs `NavigationManager.NavigateTo(uri)`, not `p.Add(...)`.
- xUnit v3 does not show `Console.WriteLine`; a temporary probe reported through `Assert.Fail(message)` (deleted).
- The API sends JSON in camelCase (`"name"`), so tests should deserialize, not string-match.

## Day 9 — Docs page for a component (2026-09-30)

Repo/branch: `ShiftFrameworkDocumentation` / `day9-shiftentityform-docs` (from master).

### What I built
`/components/shift-entity-form` (`Docs.Web/Pages/Components/ShiftEntityForm/`): `Index.razor` with `<DocPageMeta Section="Components" Status="DocPageStatus.Draft">`, three live demos (`DocComponentVariant` shows each running with its source), and `<AutoComponentDocs>` for the full parameter table. Chose ShiftEntityForm: no page existed (only ShiftAutoComplete and FileExplorer), and Day 8 taught me its internals.
- Demos (against Docs.API's in-memory Customer data): load + edit customer 1; create (no Key); the form context (`context.Mode` / `ReadOnly`).
- Sections: what it does (GET/POST/PUT/DELETE), OnSaveAction, modes, the form context, permissions (TypeAuthAction only hides; the API must enforce), migrating from obsolete `ShiftForm<,>` (Day 8).

### Things I found in the source while writing it
- No Key → the form switches itself to Create; `Endpoint` missing → `ArgumentNullException`; `TypeAuthAction="null"` → everything allowed.
- After a CREATE, the default `ViewFormOnSave` appends the new key to the browser URL (or the modal key). On the docs page that would navigate to a route that doesn't exist → the create demo uses `ResetFormOnSave`.
- **The user's own setting overrides the `OnSaveAction` parameter** (`Settings.FormOnSaveAction ?? OnSaveAction ?? default`). Documented as a warning.
- In Create mode the Save button is labelled "Create".

### How the nav works
`NavMenu` is built from `wwwroot/search-index.json`, which `Docs.SnippetGen` generates from `<DocPageMeta />`. Framework dev mode sets `RunDocsBuildHooks=false`, so I built with `-p:RunDocsBuildHooks=true`. Side effect (correct): the Mapping page's index entry, stale since the 2026-09-20 ShiftMapper rewrite (still described SHENGEN), got regenerated.

### Verified in a real browser (Chrome, Docs.API on its standard port 5231)
- Page in the side nav under **Components** with the Draft dot. Title, description, callouts render.
- Demo 1: loads John Wick read-only → Edit → Save → `PUT /api/Customer/1` 200, "Item has been Saved", back to View.
- Demo 2: empty Create → both required-field errors, nothing sent; filled → `POST /api/Customer` 201 (customer 6 created), form cleared, URL unchanged.
- Demo 3: "Mode: View / Read-only" → Edit → "Mode: Edit / Editable" + extra line; demo 1 stayed in View (each form has its own mode).
- All three "Code" toggles show the demo source.

### Problems on the way
- First run failed: I built Docs.Web but not Docs.API (host exe missing).
- Running on my own port (5299) with `--no-launch-profile` → Production mode (no WASM files, 404) and then demos called `localhost:5231` (503). The site expects its launch profile: `dotnet run --project Docs.API` (port 5231, Development).

### Checks
- `ShiftFrameworkDocumentation.sln` builds, 0 errors. SnippetGen: 12 pages in the index (was 11). Two existing pages warn "TestedAgainst 2026.09.08.3 but current is 2026.09.30.1" (not mine; the framework version was bumped). The docs repo has no test project.

## Day 10 — Challenge #2: config-driven banner component (2026-09-30)

Branches: `ShiftBlazor` / `day10-config-banners` (the component, framework code) and `ShiftTemplates` / `day10-banner-demo` (the demo). Mentor README not received: built from the task text; assumptions noted below.

### Design (ShiftBlazor)
| Piece | File | Job |
|---|---|---|
| Rule model | `Banners/ShiftBannerRule.cs` | `paths`, `include`, `exclude`, `severity`, `message` (per language) |
| Matcher | `Banners/ShiftBannerMatcher.cs` | pure logic: glob → regex, include/exclude decision, message language fallback |
| Options + registration | `Banners/ShiftBannerOptions.cs`, `ShiftBlazorBannersServiceCollectionExtensions.cs` | `AddShiftBlazorBanners(o => o.RulesPath = ...)`, same pattern as Tagging |
| Loader | `Banners/ShiftBannerRuleSource.cs` | reads the JSON from wwwroot ONCE; bad/missing file → no banners + logged warning |
| Component | `Components/Banners/ShiftBanners.razor(.cs)` | `<ShiftBanners IsConditionMet="..." />`; updates on every navigation |

Decisions / assumptions (no README):
- Globs are hand-written (~30 lines) instead of a new package: `*` one segment, `?` one char, `**` any segments; `/x/**` also matches `/x`. Case-insensitive; query/fragment/trailing slash ignored. Regex uses `RegexOptions.NonBacktracking`, so a bad pattern in the config can't freeze the browser.
- "Include/exclude uses a predicate the caller passes in" = rules list condition NAMES; the app's `Func<string,bool>` decides them. Include: at least one met. Exclude: any met hides it (exclude wins). No predicate → nothing met.
- Language: culture name (`ar-IQ`) → language (`ar`) → `en` → not shown. Same rule as the framework's `ParseLocalizedText`.

### Tests (ShiftBlazor.Tests/Banners)
- `ShiftBannerMatcherTests`: 45 (glob table of 34 cases, include/exclude, predicate calls, language fallback, ordering).
- `ShiftBannersTests` (bUnit): 7 — file loaded, only matching banners, updates on navigation, predicate used, Arabic + English fallback, file loaded once for two components, missing file → none, no registration → clear error.
- Mutation check: broke `/x/**`, removed the English fallback, removed the navigation subscription → each made the run fail. Restored.
- Full ShiftBlazor suite **261 / 261** (209 before + 52).

### Demo (StockPlusPlus)
- `wwwroot/banners.json`: invoice-month-end (include `can-write:Invoice`, en/ar/ku), product-prices (exclude `can-write:Product`, en/ar only), development-build (`/**`, include `development`).
- `Pages/Samples/Banners/BannerLayout.razor`: nested layout inside `ShiftMainLayout` that renders `<ShiftBanners>` + the page; the predicate maps condition names to TypeAuth (`CanWrite`) and the host environment. Wired through `App.razor` `MainLayout="mainLayout"` (set only under `#if (includeSampleApp)` in `App.razor.cs`) and `AddShiftBlazorBanners()` in Program.cs (sample block). `banners.json` added to the template's sample-only exclusions.
- **Seen in Chrome** (sample app on :5069, seeded dev user): `/InvoiceList` + `/InvoiceForm` → month-end + development; `/ProductList` → development only (prices rule excluded: user can write products); `/ProductBrandList` → development only; switched to Kurdish → month-end in Kurdish (RTL), development falls back to English. In-app navigation updated banners without reload.

### Problems on the way
- Pattern normalization reused the path cleaner, which cuts at `?` — but `?` is a glob wildcard. Caught while writing; patterns now have their own normalizer.
- `DefaultApp`'s `HeaderTemplate` replaces the app bar and `FooterTemplate` is at the bottom → used a nested layout instead.
- Running the sample needed its database: `dotnet ef database update` created `StockPlusPlusNET10` (24 migrations) on SQL Express.
- bUnit test helper: the mock HTTP handler is not a DI service → a small test subclass exposes it.

### Checks
- ShiftBlazor builds, 0 errors; 261/261 tests. StockPlusPlus.Web and .API build, 0 errors (with the local ShiftBlazor branch). Web.Tests 20/20; API tests 243/245 (same TestPriority failures as master).
- The demo branch needs the ShiftBlazor PR: it uses `AddShiftBlazorBanners` / `ShiftBanners`, which only exist on the ShiftBlazor branch until it is merged and released.

---

# Week 3 — Real incidents, reproduced and fixed

(Evidence in my fork, not PR candidates — except Day 15's docs page.)

## Days 11–12 — Incident A: a long sync that gets cancelled (2026-10-01)

Branch: `week3-incident-a-sync` (ShiftTemplates, from master 3bdb989). Baseline on master: 248/250 (known TestPriority).

### The incident in plain words
A sync wrote ~10,000 rows in one go. Its CancellationToken fired (timeout / disconnect / shutdown — "by user" is NOT a person) → SqlException "Operation cancelled by user" → the single transaction rolled back everything → every retry started from zero and could never finish.

### What I built (sample, plain EF tables — staging data, not ShiftEntities)
- `StockSyncInboxRow` (incoming rows, per Feed, with Status Pending/Done/Failed + Error) and `SyncedStock` (the target, unique per Feed+ExternalCode; Name max 100). Migration `AddStockSync` (only these 2 tables + 2 indexes).
- `Data/Sync/StockSyncService`:
  - `RunNaiveAsync`: load all, apply all, ONE SaveChanges — the incident, kept to reproduce it.
  - `RunAsync` (the fix): batches (default 500). Each batch's target writes AND its rows' Done/Failed marks go in ONE SaveChanges = one transaction → a batch is kept or rolled back whole. Cancellation checked between batches; a cancel during a save rolls back only that batch. Returns `StockSyncResult` (Status Completed/Cancelled, ProcessedThisRun, Done, Failed, Remaining, LastCommittedRowId, Failures) — totals read with CancellationToken.None so a cancelled run can still report. Next run = next Pending rows → resumes by itself.
  - Bad rows: validated (code/name required, quantity ≥ 0) → Failed with a reason. A row the DATABASE refuses (e.g. name > 100) → the batch is retried row by row, the refused row marked Failed ("Rejected by the database: …"), the rest committed. Failed rows are never retried, so the sync finishes.
- `POST api/stock-sync/{feed}?batchSize=&maxSeconds=` (RequireAuthorization): maxSeconds is a time budget → the run stops in time and answers Cancelled + what's left instead of hitting a gateway/command timeout. `POST api/stock-sync/{feed}/naive` reproduces the incident.

### Tests (`StockSyncTests`, 7, real SQL Server)
1. `OperationCancelledByUser_…`: token fires during a 5 s WAITFOR → SqlException containing "Operation cancelled by user" (the production message).
2. `Naive_CancelledWhileSaving_…`: 10,000 rows, cancel during the big save → 0 saved, 10,000 still pending, retry processes all 10,000 again. (Locally this surfaces as TaskCanceledException: EF sends many short commands, the token is seen between them.)
3. `Naive_OneRowTheDatabaseRefuses_…`: one null name → whole sync fails, every run.
4. ⭐ `Fixed_CancelledMidRun_…`: cancel after 4 batches → Cancelled, Done 2,000, Remaining 8,000, 2,000 targets kept; second run processes exactly the 8,000 left → 10,000, no row twice.
5. `Fixed_CancelledWhileSqlServerIsWriting_…`: cancel during a batch save → never a partial batch (Done % 2500 == 0, targets == Done); resume completes.
6. `Fixed_RowsThatCanNeverBeProcessed_…`: 4 bad rows (3 validation + 1 refused by SQL) → Completed, 996 done, 4 failed with reasons; next run has nothing to do.
7. `Endpoint_…`: POST api/stock-sync/{feed} → 200 Completed.
- Stable: 7/7 three runs in a row. Mutation: forcing one 10,000-row batch makes test 4 fail (Expected Cancelled, Actual Completed). Full suite 255/257 (248 + 7).

### Problems / notes
- First run of test 2 logged TaskCanceledException, not the SqlException; tried cancel at 400/900/1500 ms (1500: save finished first). Added test 1 to show the exact message honestly.
- Inbox Feed/ExternalCode need MaxLength: SQL Server can't index nvarchar(max).
- New snapshot dropped `.WithOne()` on ShiftIdentity's UserSecurityState relationship. Checked: untouched master had NO pending model changes and the migration has no SQL for it → a snapshot-only difference from the framework model, harmless, not mine.
- `Data/Sync/` is not in the template's sample-only exclusions (fine: evidence branch, not a PR).

## Day 13 — Incident B: the silent overwrite (2026-10-02)

Branch: `week3-incident-b-concurrency` (ShiftTemplates, from master 3bdb989). Evidence only, no PR.

### How conflict detection works today (read from the source)
- `ShiftEntityCrudHandler.PutAsync` (controllers and attribute endpoints both use it): load the row → `if (item.LastSaveDate != dto.LastSaveDate)` → 409 → otherwise `UpsertAsync` + `SaveChangesAsync`. Check and save are two separate steps.
- `ShiftRepository.UpsertAsync` calls `SetAuditFields` on the ROOT on every update — no "did anything change?" check. The save "sweep" also stamps every changed auditable row in the unit of work.
- No `IsConcurrencyToken` / row version anywhere → the SQL UPDATE checks no version.
- InvoiceRepository deletes and recreates all lines on update.

### `ConcurrencyDiagnosisTests` (4, real SQL Server, `[Trait("Category","Diagnosis")]`, each PASSES BY SHOWING THE FLAW)
1. **No-change save** (Country, api/country): tab A saves without changes → 200, same data, LastSaveDate moved → tab B's real edit → 409.
2. **Child save moves parent stamp** (Invoice, api/Invoice): tab A changes only a line price → invoice LastSaveDate moved → tab B's header-only edit → 409.
3. **Child stamps never compared**: a line-level change (new price 99, new LINE stamp, invoice untouched) → tab A saves a header edit with its stale line (price 10, old stamp) → 200, no conflict, line back to 10.
4. **Both pass, one lost** (ProductBrand via the repository, two scopes, PutAsync's steps interleaved): both load the same version, both pass the check, both commit; A's Code edit replaced by B's → A was told "saved".

### Things I learned
- Test 4 first FAILED: with B editing a DIFFERENT field, nothing was lost — EF Core only UPDATEs columns that changed on that tracked row, so different-field edits merge. The lost update needs overlapping edits. Rewrote the test; kept the finding as a comment.
- Probe: temporarily made ProductBrand.LastSaveDate an EF concurrency token → B's stale save threw `DbUpdateConcurrencyException` ("expected to affect 1 row, affected 0") and test 4 failed — so the test really detects the flaw, and that is the shape of the fix for weakness 4. Probe deleted.
- Stable 3×; full suite 252/254 (master 248 + 4).

## Day 14 — Incident C: a stale version stamp after a nested save (2026-10-02)

Branch: `week3-incident-c-stale-stamp` (ShiftTemplates, from master 3bdb989). Evidence only, no PR.

### Model
- `SalesOrder` (CustomerName, Notes, stored `Total`) with `SalesOrderLine` children (Description, Quantity, UnitPrice, IsCancelled). `RecalculateTotal()` sums non-cancelled lines. Migration `AddSalesOrder` (only these 2 tables + FK/index).
- `SalesOrderRepository`: automatic maps, lines replaced on update (as InvoiceRepository), Total recalculated on every save.
- `SalesOrderController` (`StockPlusPlusActionTree.SalesOrder`) + action `POST api/SalesOrder/{key}/lines/{lineKey}/cancel`: cancels the line (child) and recalculates Total (parent), saved through the repository → BOTH get a new LastSaveDate.
- `Pages/SalesOrder/SalesOrderForm.razor`: the parent form with the lines table and a "Cancel line" button.

### Reproduce → fix (tests: `SalesOrderStaleStampTests`, 3, HTTP + SQL Server)
1. `Reproduction_AFormThatKeepsItsCopy…` → 409 although nobody else touched the order. Passes before AND after (the old stamp really is out of date — must stay refused).
2. ⭐ `AfterTheLineAction_TheFormHoldsTheCurrentOrder_SoTheNextEditSucceeds` — BEFORE: FAILED "Expected 200, got 204" (the naive action answered 204, the form had no way to learn the new version). AFTER: passes.
3. `AfterTheLineAction_AnEditBySomeoneElse_StillCausesA409` — the fix doesn't weaken conflict detection.
- The fix: the action answers 200 with the ORDER as it now is (`ShiftEntityResponse<SalesOrderDTO>`, same shape as GET: lines, Total, new LastSaveDate); the form takes that version. In the page: ShiftEntityForm has no public way to take a new version (SetValue/FetchItem are internal), so the page bumps `@key` → Blazor rebuilds the form, which loads the current order.

### Where the fix belongs (5 lines — also in `Pages/SalesOrder/INCIDENT-C.md`)
1. Now (app layer): an action that changes the order answers with the current order and the form takes it; the conflict check stays strict.
2. Not by loosening the check: the old copy IS stale — accepting it would send the line back uncancelled (child stamps aren't compared, lines are replaced).
3. Long run (framework): ShiftEntityForm needs a public way to take a new version after an action, and a framework action helper should return the updated root by default.
4. Also framework: stamp the root only on real changes, and compare a database-checked version (rowversion / concurrency token per aggregate) — Day 13's weaknesses 1–4 are the cause.
5. Why both: the app fix is local and needed today; every app with child actions hits the same trap, so the default must change where the stamping and the form live.

### Checks / problems
- Stable 3×; full suite 251/253 (master 248 + 3). Web.Tests 21/21 (renders SalesOrderForm). API, Web: 0 errors.
- Browser check stopped: the login button sent no request (identity login flow changed on master since Day 10). Stopped after repeated attempts instead of poking further; server behaviour is covered by the tests.
- The local app database `StockPlusPlusNET10` now has this branch's `AddSalesOrder` migration (plus master's AddSamplePiiContact).

## Day 15 — Challenge #3: the write-up (2026-10-02)

Repo/branch: `ShiftFrameworkDocumentation` / `day15-concurrency-guide` (from master 78b2d7d). This one IS a PR.

### What I wrote
`/guides/concurrency` — "Concurrency in Shift Framework" (Guides, Order 40, Status Draft, TestedAgainst 2026.09.30.2). Sections: In two minutes · What LastSaveDate does · What it does not do (the 4 weaknesses + "different fields merge") · Trap: an action that changes a child (Incident C) · Trap: a long write that gets cancelled (Incident A, with the recipe) · Trap: two editors at once (Incident B) · Checklist · Where the framework could go. ~1,500 words prose + ~110 lines of code ≈ 10 minutes.

### Compile-checked code (Docs.Verify/Concurrency, like DataLevelAccessV2)
- `ResumableSyncRecipe.cs`: snippets ConcurrencyResumableInbox, ConcurrencyResumableSyncLoop, ConcurrencySyncEndpoint (the Incident A recipe, distilled from StockSyncService; loads targets once per batch).
- `ActionReturnsCurrentRoot.cs`: snippet ConcurrencyActionReturnsCurrentRoot (Incident C fix) — compiles against ShiftEntity, ShiftRepository, ShiftEntitySecureControllerAsync, so a framework change breaks the build instead of silently staling the docs.
- Only the framework excerpt and the Blazor @key part are inline DocCodeBlocks (illustrative).

### Facts checked before writing them
- Minimal APIs (`MapShiftEntitySecureCrud`) call the same `PutAsync`.
- Nothing in the framework catches `DbUpdateConcurrencyException` → the "database-checked version" direction says it needs framework support first.
- Existing docs (ShiftEntityModel, Repositories) never explain the conflict check.

### Checks
- Concurrency.Verify builds, 0 errors; docs solution builds; SnippetGen 21 snippets (+4), index 12 pages: ONLY `/guides/concurrency` added (+ the stale Mapping entry refreshed again, as on Day 9, because master's index is still stale until docs PR #1 merges).
- Viewed in Chrome (Docs.API standard profile): nav Guides → Concurrency in Shift Framework with Draft dot, verified-against badge, 8 sections, 6 code blocks, 4 callouts, `@key` shown correctly.

# Week 4 — Flagship C: Zero to Reviewed

Goal: bring the 6 Get Started pages to Reviewed against the current release, using Docs.Verify scripts; record a 5-minute capture from scaffolding to a running app; landing page; Day 20 CEO walkthrough.
Plan: Day 16 pages 1–2 · Day 17 pages 3–4 · Day 18 pages 5–6 + one PR · Day 19 video · Day 20 landing page + walkthrough.

## Day 16 — What is Shift Framework? + Installation (2026-10-11)

Repo/branch: `ShiftFrameworkDocumentation` / `day16-get-started` (from master 9a01733). ShiftTemplates updated to master b25209c. Current release: **2026.10.10.2** (= latest ShiftSoftware.ShiftTemplates on NuGet). Commit c21498b (local, PR on Day 18).

### Installation — re-verified
- Ran `Docs.Verify/Scripts/install-and-scaffold.sh` unchanged in a scratch folder, starting from my old Week 1 template install (2026.9.8.3): template replaced with 2026.10.10.2, project scaffolded, `dotnet build` **0 errors, 38 warnings**. Script exit 0.
- Checked: `dotnet new shift -n X` without the two parameters → "Missing mandatory option(s): '--includeSampleApp', '--shiftIdentityHostingType'" → the page's "two parameters are required" is right.
- Checked: template's default connection string is `localhost\sqlexpress`; Run the sample already explains the LocalDB override → Prerequisites line kept.
- Fixes:
  1. "Visual Studio 2022+" → "2026+" (.NET 10). The repos' newest .sln files are saved by VS 18 (= 2026).
  2. Upgrade tip: the plain install command already replaced 2026.9.8.3 with the latest, so `@*` is the optional explicit form, not required. Troubleshooting bullet reworded the same way. (Snippet kept — it's still a valid command.)
  3. "A clean build" → "a build with 0 errors" + a Note: a fresh project has a few dozen ShiftMapper SM00xx warnings (SM0030 ×52, SM0001 ×12, SM0036 ×8, SM0018 ×2 + 2 CS8602 across both projects' builds); they're expected.
  4. "Do not run yet" callout said "the next page covers running the sample" — the next page is Template parameters → now links to Run the generated sample.
- TestedAgainst 2026.09.08.3 → 2026.10.10.2 (SnippetGen's "re-verify the page" warning for it is gone).

### What is Shift Framework? — Draft → Reviewed
- No commands/code on the page → verified by fact-checking against a freshly scaffolded project and the repos:
  1. "DTO conventions (View / List / Upsert)" was wrong: the template has `ProductDTO` (form) + `ProductListDTO` (list), no Upsert DTOs → reworded.
  2. ShiftMapper missing although every generated project references it (replaced the ShiftEntity generator, see Day 6) → added, described from its README.
  3. Other claims checked OK: ShiftEntity OData (used by controllers), ShiftIdentity companies/branches, TypeAuth, ShiftBlazor.

### Checks
- `dotnet build Docs.Web -p:RunDocsBuildHooks=true`: 0 errors. Search index: only `/get-started/installation` and `/get-started/what-is-shift-framework` changed (compared entry by entry). Remaining TestedAgainst warning is `/guides/data-level-access` (not in scope).
- Viewed in Chrome (Docs.API): both pages show the blue Reviewed chip and "Last verified against Shift Framework 2026.10.10.2"; all code blocks render; all Get Started links point to existing routes.

### Notes / problems
- Only created the docs branch and fast-forwarded ShiftTemplates master today. The other 8 repos are still on older master — not needed for docs pages so far.
- For Day 18: a new project ships with `CosmosDb.Enabled: true` in appsettings.Development.json, but Installation calls the Cosmos emulator optional and Run the sample doesn't mention it. Check when actually running the app.

## Day 17 — Template parameters + Project tour, and a template bug fix (2026-10-11)

Docs: `ShiftFrameworkDocumentation` / `day16-get-started`, commit 2173fc4 (local). Template fix: `ShiftTemplates` / `day17-template-combos-build` (from master b25209c), commit b2df400 (local). All 10 repos now on latest master (the 8 framework repos fast-forwarded — all were clean, no local-only commits).

### ⭐ Found: two template combinations don't build on 2026.10.10.2
Scaffolded and built every combination of `--includeSampleApp` × `--shiftIdentityHostingType` from the NuGet template:
| Combination | Before | After fix |
|---|---|---|
| sample + Internal (default) | ✅ | ✅ |
| sample + Internal + Functions + Test | ✅ (0 errors) | — |
| no sample + Internal | ❌ CS0103 `ShiftIdentityActions` in `Shared/NavMenu.razor` | ✅ |
| any + External | ❌ CS0234 ×2, `Development/DevelopmentTools.razor` uses the ShiftIdentity Dashboard layout, which External doesn't reference | ✅ |
- Causes: NavMenu's `@using ShiftSoftware.ShiftIdentity.Core` sat inside `@*#if (includeSampleApp)*@` but is used outside it. `DevelopmentTools.razor` (added 2026-09-12, 8375c21) was never excluded for External.
- Fix (3 files, +8/−3): move the using out of the block; add `DevelopmentTools.razor` to template.json's `(!internalShiftIdentityHosting)` exclude (next to SendEmailService.cs); wrap the two `development/tools` links (NavMenu, Index) in `@*#if (internalShiftIdentityHosting)*@`.
- Proof: copied the changed template to a scratch folder, set the versions from ShiftFrameworkGlobalSettings.props (what the Builder does), `dotnet new install <folder> --debug:custom-hive` (my installed template untouched), scaffolded the 4 combinations → 0 errors each; default still has the dev-tools page + both links, External has neither. Sample itself: StockPlusPlus.Web builds, StockPlusPlus.Web.Tests 23/23.
- No open issue for it on GitHub. PR to open on Day 18 with the docs PR.

### Template parameters — Draft → Reviewed
- New `Docs.Verify/Scripts/template-parameters.sh`: scaffolds the page's combinations (+ no-sample/Internal) into a temp folder, builds each, checks: Functions/Test only with the flags; Test points at `localhost\sqlexpress`; API port in 5000–5300, Functions in 7000–7300; fresh UserSecretsId; external URLs land in Web `wwwroot/appsettings.Development.json`. The two broken combinations are EXPECTED to fail with the known errors (prints a NOTICE when they start building → remove the page's Warning). Passes on 2026.10.10.2.
- Fixes: identity URL defaults are `http://localhost:5069` (page said empty) and they land in the Web project's appsettings.Development.json (`ShiftIdentityApi` gets `/api/`), not "appsettings.json"; `--addTest` = real SQL Server Express (`localhost\sqlexpress`, recreates `YourApp_Test`), not "in-memory"; mapper examples updated ([ShiftEntityMapper] partial class no longer exists); the two inline `<pre>` commands → snippets from the script; Warning for the known issue.
- Checked OK: two required params, port ranges (5 scaffolds: 5209, 5018, 5229, 5051, 5234 — one test scaffold landed on 5069 by chance), project GUIDs replaced, user secrets.

### Project tour — Draft → Reviewed
- New `Docs.Verify/Scripts/project-tour.sh` (no commands on the page → checks its structural facts against MyFirstShiftApp: sln projects, references, folders, settings files, DB base class, endpoints). Passes.
- Fixes: diagram showed Web → API → Data → Shared as a stack; real references: API → Data + Web (it hosts the Blazor app), Web → Shared, Data → Shared. Added E2ETests folder (puppeteer, not in the sln), Shared/Localization, Web/Development. DTOs = view + list (not View/List/Upsert). DbContext is `DB` on ShiftIdentityDbContext/ShiftDbContext. `ShiftRepository<DB, TEntity, TListDTO, TViewDTO>`. Permission checks are in the endpoint layer (Day 7!), not the repository. Only appsettings.Development.json ships. Reports rendered by ProductCategoryRepository. Request flow: ProductController is `[NonController]` → rewritten for `ProductEndpoints` (MapShiftEntitySecureCrud).
- My own wrong claim caught by the script: "Country has no repository" — CountryRepository exists as an automatic-mapping demo; its api/country endpoints use the built-in repository. Reworded.

### Checks
- Docs build 0 errors; new snippets ScaffoldWithFunctionsAndTests / ScaffoldSkeletonExternalIdentity generated; search index: only the two pages changed. Viewed both in Chrome: Reviewed chips, warning callouts, snippets, diagram.

### Problems
- First browser check: page stuck at 91% "An unhandled error has occurred". Cause: I started Docs.API with `--no-build` after ShiftBlazor was updated → stale asset list; after restarting properly Chrome still used cached files (Blazor asks for `force-cache`) → integrity mismatch "Failed to fetch". Server verified fine (hashes match); opened the site at `127.0.0.1:5231` (fresh cache) → OK. Lesson: always `dotnet run` with a build after updating framework repos.

## Day 18 — Run the generated sample + Your first entity (2026-10-11)

Docs branch `day16-get-started`: commits 87e13ac (pages 5–6) and 8fb5948 (CRLF-safe snippet reading). All 6 Get Started pages are now Reviewed against 2026.10.10.2.

### Run the generated sample — Draft → Reviewed
- Ran the old `run-sample.sh` unchanged → passed, but it only checked "the API answers". Tried the app as a beginner in Chrome: login SuperUser / OneTwo ✅, created a Product Brand ✅ ("Item has been Created").
- Findings:
  1. New project has `CosmosDb.Enabled: true`; without the emulator the app still works, but the console shows 6 red `fail: … CosmosDB Syncing Failed … (localhost:8081)` lines at first run (seed replication). With `CosmosDb__Enabled=false` → 0 fail lines. Page said nothing → new section + DisableCosmos snippet; Installation's Cosmos bullet links to it (the Day 16 note, resolved).
  2. "Verifies migrations are up to date" — nothing in Program.cs does that → removed. Proved what happens on an empty DB: `Invalid object name 'ShiftIdentity.Countries'` (the page's error is right).
  3. Port "typically 5069" → it's the random scaffold port (this project 5089).
  4. Missing-Development error: page said "Token public key cannot be null"; real: `Value cannot be null. (Parameter 's')` at `Convert.FromBase64String` in `AddShiftIdentity` (hit it myself by setting the env var after starting the API).
- `run-sample.sh` upgraded: LocalDbConnectionString + DisableCosmos JSON snippets sit in a quoted heredoc (bash never runs them, SnippetGen extracts them) and the harness reads them BACK out of the file and uses them → the shown config is the tested config. New checks: seed (SuperUser/IQ/KRG/SFT via sqlcmd), login through `POST api/identity/v2/login` (PKCE code challenge, 43 chars) → `"kind":"session"`, wrong password → `"kind":"refused"` 400, no `fail:` lines.

### Your first entity — Draft → Reviewed
- ⭐ The tutorial's `dotnet new shiftentity -n Vehicle` FAILS on the sample: "Creating this template will make changes to existing files" (the sample already has Vehicle; `--force` would overwrite it). The old script hid it by `rm -rf`-ing the sample's own Vehicle files first. → Tutorial now builds a **Warehouse** (Name, City, Capacity).
- 8 files, not 9 (no mapper file since Stage 4b). Generated form is EMPTY and the list shows only ID → new Step 4 adds the form fields + list columns. TypeAuth note named `StockPlusPlusActionTree` → `MyFirstShiftAppActionTree`, and now says plainly the entity is open to any signed-in user until an action is added.
- Template quirks noted (not changed): repository comment says "(Warehouse's list Code and write AfterMap are there)" — leftover from ProductBrand renamed by sourceName; forms use `BaseUrlKey="StockPluPlus"` (also in the sample, harmless — saving works).
- `add-first-entity.sh` rewritten: scaffold → checks (8 files, no mapper, base(null), TypeAuthAction="null", DbSet partial) → writes the page's entity/DTO snippets into the files and inserts form fields/list columns → build → AddWarehouse migration → sign in → `POST api/Warehouse` 201 → list returns it → cleanup (stops API, drops DB, removes Warehouse files + migration, restores the snapshot).
- Problem: first run "InitialCreate failed" — I created the database AFTER adding Warehouse to the model; EF refuses `database update` with pending model changes. Fixed the order (the reader arrives from page 5 with a migrated DB).
- Checked by hand in Chrome too: /WarehouseList → + → Name/City/Capacity → Create → row 1 "Main Warehouse, Erbil" in the list. (Had to log in again: localhost cookies are shared across ports, the old app's session showed "session expired".)

### Final check
- All five scripts in order from an empty folder: install-and-scaffold ✅ (22s), template-parameters 13 ok (38s), project-tour 22 ok (1s), run-sample 7 ok (33s), add-first-entity 9 ok (56s) — 0 FAIL, nothing left behind.
- Windows line endings: Git here has autocrlf → scripts get CRLF in a checkout. Git Bash runs them fine, but I made `snippet_body` strip CRs anyway and tested extraction on CRLF copies (correct values, no stray `\r`). Lesson: test with the real file's lines, not retyped ones (backslashes get lost when retyping).
- Docs build 0 errors; search index: only the six Get Started pages changed; all pages viewed in Chrome.

### PRs (opened 2026-10-11)
- ShiftTemplates #13 — https://github.com/ShiftSoftware/ShiftTemplates/pull/13 — `day17-template-combos-build`, 1 commit, 3 files +8/−3, mergeable.
- ShiftFrameworkDocumentation #3 — https://github.com/ShiftSoftware/ShiftFrameworkDocumentation/pull/3 — branch renamed `day16-get-started` → `get-started-reviewed`, 4 commits, 11 files, mergeable; links #13 for the known-issue Warning.
- gh was logged out: pushed with git (saved credentials), then `gh auth login --web` (one-time code approved in the browser).

## Day 19 — Recording prep: zero to running app (2026-10-11)

- Rehearsed the video's exact commands in a fresh folder (Git Bash, NuGet template 2026.10.10.2): scaffold 5s · build 18s · migrations add 7s · database update 11s · run until "Now listening" 11s → ~1 min of waiting; the rest of the 5 minutes is talking and clicking. 0 `fail:` lines with Cosmos off.
- Rehearsal problem (useful for the video): an edit wrote `(localdb)\MSSQLLocalDB` with ONE backslash → invalid JSON → `Failed to load configuration from file …appsettings.Development.json` then `Unable to create a 'DbContext' of type 'DB'`. That's the most likely beginner slip on camera → in the script's "if something goes wrong" table; the commands file has the line ready to paste (checked: two backslashes, parses as JSON).
- Recording tool: Snipping Tool (Win+Shift+R, whole screen, MP4) — NOT Xbox Game Bar (records one window only; the video switches terminal → VS Code → browser). Browser: Incognito, to avoid stale localhost sessions (Day 18).
- Prepared: empty `C:\Demo`; LocalDB has no `MyFirstShiftApp` database; template 2026.10.10.2 installed.
- Files: `TestProject/day19-recording-script.md` (timed table: do / say, troubleshooting, clean-up for a retake) and `TestProject/day19-commands.txt` (copy-paste commands).
- For Day 20: no ffmpeg on this PC → the landing page GIF needs either ffmpeg (winget) or a GIF recorded in Chrome.

### Day 19 — status 2026-10-12 (video NOT recorded yet)
- Prep re-checked: `C:\Demo` empty, no `MyFirstShiftApp` database in LocalDB, installed template still **2026.10.10.2**.
- ⚠️ New upstream release **2026.10.11.1** (ShiftTemplates d317358 + bump 33847f3): template.json now always excludes `StockPlusPlus.Web/Development/**` (and E2ETests), plus edits to NavMenu.razor / Index.razor / App.razor / Program.cs. Overlaps PR #13 (same files → may conflict / may already fix the External build) and makes Project tour's "Web/Development" mention stale for the new release.
- Decision: record the video on the installed 2026.10.10.2 (matches the docs' "verified against" label) — do NOT run `dotnet new update` before recording.
- Next: (1) record the video, following day19-recording-script.md; (2) re-verify Get Started scripts on 2026.10.11.1 + check PR #13 against the new master; (3) Day 20 landing page.

### Day 19 — status 2026-10-12 evening (still NOT recorded)
- Correction: `C:\Demo` is NOT empty. It has a `MyFirstShiftApp` project scaffolded at 04:02 on 2026-10-12, with no migrations. LocalDB has no `MyFirstShiftApp` database, and no video file was found. Before recording, delete `C:\Demo\MyFirstShiftApp` (retake clean-up).
- Installed template is still **2026.10.10.2** ✅. Upstream now has **2026.10.12.1**, one more release after 2026.10.11.1. Check both against PR #13 after recording.

### Day 19 — status 2026-10-13
- Re-checked: installed template still **2026.10.10.2**; no `MyFirstShiftApp` DB in LocalDB; no recording in Videos yet. Upstream still at 2026.10.12.1 (f8fed59). PRs ShiftTemplates #13 and ShiftFrameworkDocumentation #3 still open, no reviews or comments.
- Deleted the leftover scaffold `C:\Demo\MyFirstShiftApp` (an untouched scaffold from 2026-10-12 04:02). `C:\Demo` is empty again and ready to record.

## Day 19 — Video recorded (2026-10-14)

- Recorded the whole screen with ffmpeg (installed with winget, Gyan.FFmpeg 9.0.2). No voice; captions for each step are burned into a dark strip under the picture.
- Files: `Videos\zero-to-running-app.mp4` (3:21, 1920×1170, with captions) and `zero-to-running-app (no captions).mp4` (1920×1080, for talking over).
- Flow = day19-recording-script.md on template 2026.10.10.2: dotnet --version → dotnet new list shift → scaffold (port 5265) → build (0 errors, 38 warnings) → VS Code: SQLServer line pasted from day19-commands.txt + Cosmos `Enabled: false` → migrations add → database update → dotnet run → Chrome Incognito → SuperUser/OneTwo → Products > Brands > + → "Shift Coffee / Shift Kahve / SC-01" → "Item has been Created" → row in the list. Checked in LocalDB: `dbo.Brands` row 1 `{"en":"Shift Coffee","tr":"Shift Kahve"}` SC-01.
- Reviewed the video frame by frame: no typos, no notification pop-ups. Visible: Windows "Activate Windows" watermark, and an unrelated `log.md ●` tab in VS Code.
- Left behind on purpose: `C:\Demo\MyFirstShiftApp` and the `MyFirstShiftApp` LocalDB database (app stopped). Clean up for a retake before re-recording.
- Takes 1–3 were thrown away (recording problems, not the docs): first keystroke after switching windows lost (`lear`, `otnet`); Chrome zoom different from rehearsal → missed click.
- ⚠️ Possible finding (seen once in 3 fresh runs): take 2, first page load right after `dotnet run` → "Loading..." + "An unhandled error has occurred. Reload"; a reload fixed it; takes 3 and 4 loaded fine. Not reproduced yet — check the browser console if it happens again; may deserve a line in "Run the generated sample".
- Next: re-verify Get Started on 2026.10.12.1 + check PR #13 against new master; Day 20 landing page (ffmpeg is now installed for the GIF).

### Day 19 — video re-recorded, more human (2026-10-14)
- Re-recorded with slower typing and more mouse use. New take (5:09): typing ~45–60 wpm; switching apps by clicking taskbar buttons (Chrome → Incognito preview); clicking Chrome's address bar; in VS Code clicking line 15 (End, Shift+Home, paste) and double-clicking `true` → `false`; pointer kept still while commands run.
- The system pointer records badly (text cursor = white block or invisible), so it's left out of the capture and a clean arrow + click rings are drawn onto the video afterwards.
- Files: `Videos\zero-to-running-app.mp4` (captions) and `zero-to-running-app (no captions).mp4`; the first version is kept as `... - old.mp4`.
- Seen in the video: Chrome's address-bar suggestions list earlier localhost test pages (e.g. localhost:5272/VehicleList). The first-load error didn't happen this time.
- `C:\Demo` again holds the recorded project + DB (app stopped).

## Week 4 wrap-up — re-verified on 2026.10.12.1 (2026-10-14)

- Ran the five Get Started scripts on NuGet template **2026.10.12.1** (installed by install-and-scaffold.sh, replacing 2026.10.10.2). First run: install ✅, run-sample ✅, add-first-entity ✅; template-parameters ✅ with 2 NOTICEs; project-tour ❌ 2 FAIL.
- ⭐ Upstream fixed both template bugs from Day 17 (commit d317358 "exclude shiftFrameworkDevelopmentMode files…", in 2026.10.11.1+): skeleton-external and skeleton-internal now build. → **PR ShiftTemplates #13 is no longer needed** (it still merges cleanly, but upstream fixed the same thing differently: excludes all of Web/Development).
- New projects no longer have `E2ETests/` or `Web/Development/` → Project tour was stale.
- Docs changes (branch get-started-reviewed, commit 47aaf7c, local, not pushed yet): Template parameters: removed both known-issue Warnings, added a Note for readers on ≤2026.10.10.2 ("newer releases fix this, checked on 2026.10.12.1", link to Installation). Project tour: removed the E2ETests paragraph/section/keyword and the Web "Development" bullet. Scripts: template-parameters.sh requires the skeleton builds to pass (build_must_pass, NOTICES removed); project-tour.sh checks E2ETests and Web/Development are absent. TestedAgainst → 2026.10.12.1 on all six pages.
- Proof: all five scripts in order from an empty folder: 13 + 23 + 7 + 9 ok, 0 FAIL, nothing left behind. Docs build 0 errors. SnippetGen: current version 2026.10.12.1, only `/guides/data-level-access` stale (not mine).
- Lesson: SnippetGen reads the "current version" from the sibling `ShiftTemplates/ShiftFrameworkGlobalSettings.props`, so ShiftTemplates must be on master, not a PR branch. Switched ShiftTemplates to master (fast-forward to f8fed59).
- Lesson: re-running project-tour.sh after run-sample.sh fails "Migrations folder starts empty" — run the scripts in order from an empty folder.
- `C:\Demo` cleaned (video project + DB removed; the videos are in my Videos folder).
- Not done: visual check of the two pages in the browser (not done yet).
- Next: push 47aaf7c to the fork (updates PR #3); comment on + close PR #13. Then Day 20: landing page (recommended: new public repo on DahenENG, built locally first) + CEO walkthrough; Week 4 report.

## Day 20 — Landing page (2026-10-14)

- PR #3: pushed 47aaf7c to the fork (5 commits) + comment explaining the 2026.10.12.1 update. PR #13: comment ("fixed upstream, all 4 combinations build on 2026.10.12.1") and closed.
- Landing page built locally: `C:\Projects\zero-to-reviewed` (git repo, branch main, commit b3d8cdd, no remote yet). Single `index.html` (no build step, light/dark), `assets/` = captioned video (23 MB) + poster + 5 screenshots taken from the video.
- Sections: hero + stats (6 pages, 5 scripts, 52 checks, 2 template bugs, ~5 min) · video + screenshots · how it works (script → SnippetGen → page) · page by page · bugs found (combinations table; Vehicle tutorial) · Week 4 timeline · links.
- Checked in Chrome at desktop 1280 px and 390 px phone width → scrollWidth 390, no sideways scroll.
- Next: create the public repo and turn on GitHub Pages. Still to do: CEO walkthrough outline, Week 4 report.
- Published: public repo https://github.com/DahenENG/shift-internship-week4 → GitHub Pages **https://daheneng.github.io/shift-internship-week4/** (built, HTTP 200, video streams). Repo name kept neutral so the page title ("Zero to Reviewed") can change later without breaking the link: edit `index.html` → commit → push, live in ~1 min.
- CEO walkthrough script: `TestProject/day20-walkthrough.md` (about 10 min: timed table with what to say, video chapter times, likely questions + answers, what to do if something fails, a checklist for before the meeting).
- Week 4 report: `TestProject/Shift-Week4-Report.pdf` (22 pages, same layout as Weeks 2–3: Parts A–L) + a copy on the Desktop. The source is in `TestProject/week4-report-source.py` (ReportLab; the Week 3 generator wasn't on disk any more).
- Noticed: PR #3's title still says "against 2026.10.10.2" (the comment explains the update). Week 1 branch `week1-docs-updates` still unpushed.
- **Week 4 complete** apart from presenting the walkthrough.

## End of 2026-10-14: saved for tomorrow

- Re-checked before saving: every repo has a clean working tree (Docs `get-started-reviewed` @ 47aaf7c, ShiftTemplates master @ f8fed59, zero-to-reviewed main @ 14afcbc). PRs: Docs #1 #2 #3 open, ShiftTemplates #9–#12 open (#13 closed), ShiftBlazor #3 open. NuGet template is still 2026.10.12.1. `C:\Demo` is empty.
- Wrote `TestProject/tomorrow.md` (+ a Desktop copy, `Shift-tomorrow.md`): a list of things to check (PR replies, a new template release, the PR #3 title, the landing page title, the Week 1 branch, the GitHub email) and what to study for the walkthrough.

## Review fixes (2026-10-19)

Seniister reviewed all 8 open PRs on 2026-10-18: one comment each (no line comments, no approve/request-changes). Upstream master is now at 2026.10.15.1, 29 commits ahead of my ShiftTemplates branches, so each fixed branch is rebased onto it.

Baseline: full `StockPlusPlus.Test` on untouched master = 246 / 252. The 6 failures: `TestPriority.One/Two`, `IdentityReplicationMapperRegistrationTests.ExpectedPairs...`, `ReplicationMappingParityTests.User`, `IdentityCosmosSetupTests.Create...`, `IdentityReferenceSourceTests.Benchmark...`.

- **ShiftTemplates #11** ("exclude Pages/Samples/** from the template"): added `StockPlusPlus.Web/Pages/Samples/**/*` to the template's exclude list (conflicted with 2 new upstream lines in the same place; kept all). Template installed from the branch into a custom hive: before the fix a fresh `dotnet new shift` had `Pages/Samples/SideBySideForms/` (3 files), after it nothing. Fresh projects build with 0 errors (`--includeSampleApp true --addTest true`, and `--includeSampleApp false`). Web.Tests 27/27. Pushed + reply.
- **ShiftTemplates #9** ("stale test comment" + "Day 6 diagnostics note never written"): `VehicleMappingTests` comments no longer mention the deleted generator / `IgnoreEntity`; they now point at `StockPlusPlusMapper`'s write map. The diagnostics note existed only here in log.md (git-ignored, the reviewer can't see it) and in the Week 2 report, so it was added to the PR description ("Diagnostics (Day 6 task)"). Removing the 4 `Ignore()` calls makes the audit test fail. Full test 252/258 (same 6). API + Web 0 errors. Pushed + reply.
- **ShiftTemplates #10** ("add a Read+Write user and prove delete returns 403"): new test `ReadWriteUser_CanCreateAndUpdate_ButTheApiRefusesDelete`. With `base(null)` the delete returns 200 and the car is deleted, so the test catches it. Master had 5 new migrations after the old `AddCar` (2026-09-27), so the old one was dropped and `AddCar` regenerated on top (only `Cars` + `CarsHistory`; `has-pending-model-changes` clean). Car tests 4/4, full test 250/256 (same 6). Pushed + reply.
- Still to do: ShiftBlazor #3 → ShiftTemplates #12 → Docs #1, #2, #3.
- **ShiftBlazor #3** (null crash, narrow catch, failed load cached): nulls in the rules file are dropped at load; any exception is caught (URL building moved into the `try`); a failed load isn't cached and the component retries on the next navigation. 4 new tests, all fail on the old code. Full suite 331/331 (master 275). Pushed + reply. (Updated local ShiftEntity/ShiftIdentity/TypeAuth to master: ShiftBlazor master needs the newer ShiftEntity.)
- **ShiftTemplates #12** ("blocked until ShiftBlazor #3", "typo fails silently", "text claims behaviour"): moved `Pages/Samples/Banners` → `Pages/Banners` (otherwise #11's exclusion would break sample-app projects); `MainLayout` on both `DefaultApp`s (master now has a development-mode one); unknown condition names logged once with the known list; honest texts (ids `invoice-editors`, `product-viewers`). `BannerLayoutTests` (2): real banners.json + typo; each proven to fail when broken. With #11 merged in: `--includeSampleApp false` builds 0 errors; `true` has the right files, and its only error is the missing `ShiftSoftware.ShiftBlazor.Banners` (NuGet ShiftBlazor doesn't have it until ShiftBlazor #3 ships). Web.Tests 25/25. Pushed + reply. Still to check: the new Kurdish banner text.
- **Docs #1** (Print, CloseFormOnSave, Embedded wrong; Car instead of Vehicle): checked in ShiftBlazor source. Print is hidden unless `ShowPrint`, View/Archive only, `print-token` then `print` in a new tab, `PrintConfig` → options dialog. `CloseFormOnSave` only acts in a modal (closes it, returns the item, no message); outside a modal nothing happens after save. `Embedded` = don't take over the page; NOT read-only (compare view passes it with `Mode=Archive`). Example now uses `Vehicle`. Rebased; search-index conflict → took master's and regenerated (only this page added). Docs build 0 errors. Pushed + reply.
- **Docs #2** (child-save contradiction, wrong mapper claim, recipe missing 3 safeguards): checked `ShiftRepository` — the parent's PUT always re-stamps the root (`UpsertAsync`), the save sweep stamps only modified rows, so a child changed by its own path moves only its own stamp; weakness 2/3, the summary and Incident C's "why" now say which path does what. Mapper claim removed (EF writes only changed values). Recipe safeguards (my reading; the comment didn't name them, asked in the reply): one run per feed (SQL Server app lock on the run's connection + unique key on Feed/ExternalCode), only row-caused SQL errors (2627/2601/547/515/8152/2628) mark Failed, server-side time budget always (default 60, max 120 s). Ran the recipe against SQL Express: AlreadyRunning / refused row Failed / other error leaves Pending. The run caught `new SqlParameter("@LockTimeout", 0)` → sends NULL (0 = SqlDbType) → waits forever; fixed. Pushed + reply.
- **Docs #3** (stale title/description, one release behind, a script that only works in a certain order): TWO scripts were order-dependent — `add-first-entity.sh` only after `run-sample.sh` (needed its InitialCreate; now adds it itself, idempotent like run-sample), and `project-tour.sh` only before it ("Migrations folder starts empty"; now scaffolds its own fresh project in a temp folder with the ScaffoldFirstProject command read from install-and-scaffold.sh). Upgraded the local template to 2026.10.15.1 (`dotnet new install ShiftSoftware.ShiftTemplates@*`). Documented order from an empty folder: all pass (13 / 23 / 7 / 9 ok). Different order (add-first-entity before run-sample, project-tour after): all pass; the OLD versions fail there (control). `TestedAgainst` 2026.10.15.1 on all six pages (+ the Note's "checked on"). Search index: only the six pages changed. Title + description rewritten (also fixed the stale #13 / E2ETests lines). No leftover DBs/processes. Pushed + reply.
- **All 8 review comments answered (2026-10-19).** Every PR: MERGEABLE, a reply on each. Local ShiftTemplates and ShiftBlazor back on master (pulled; ShiftTemplates master = 2026.10.15.1). ShiftEntity / ShiftIdentity / TypeAuth also pulled to master.
- Waiting on: Seniister's next review. Open: check the new Kurdish banner text (ShiftTemplates #12, wwwroot/banners.json); Docs #2's "three safeguards" are my reading (asked the reviewer in the reply).

## Mentor feedback on ClickUp (2026-10-19)

Sarmad Ahmed commented on Week 4 (landing page review, "fix before the walkthrough") and on Week 3 (Incidents A, B, C, with line comments).

### Done today: landing page (https://daheneng.github.io/shift-internship-week4/, commit 09144ed)
- Privacy: settings.jpg cropped to the ConnectionStrings/CosmosDb lines (Cosmos key blurred; RSA keys, the log.md tab and the status bar gone); build.jpg cropped above the prompt (no Windows username); poster.jpg prompt blurred. The video had the same problems: processed every frame (Python, numpy + PIL through ffmpeg pipes): green prompt lines at the left edge blurred in terminal frames; in VS Code frames the Cosmos key line, lines 23-44 (token keys) and an extension label in the status bar blurred. Checked the open/close transitions at 6 fps. The last caption "Six Get Started pages: Reviewed" replaced with "...: Reviewed in Docs PR #3 (open)".
- Content: What I shipped (9 PR cards: link, why, "open, not merged" / #13 "closed, not merged"); Docs I wrote; What I'd do next; video transcript (17 captions with times, read from the video); stats and chips say "Reviewed in Docs PR #3 (open)"; template bug card = two bugs, three of four combinations, 4-row table; Day 17 wording (#13 closed, fixed upstream first); overclaim and slogan removed; "0 errors, 38 warnings"; phone table scrolls (checked: scrollWidth 375 at a 390px frame).
- Template combinations rebuilt on 19 Oct to make the table exact: 2026.10.10.2 → sample+Internal builds, no sample+Internal CS0103, sample+External CS0234, no sample+External CS0234; 2026.10.15.1 → all four build. (First attempt named the project `App`, which clashes with the `App` class → hundreds of CS0426; use `-n MyApp`.)
- Docs #3 description: "two template bugs broke three of the four combinations".
- Side finding: template 2026.10.15.1 ships `.claude/settings.local.json` (another developer's local paths) in every generated project. Worth telling the team.

### Done 2026-10-20
- CONTRIBUTING.md: https://github.com/ShiftSoftware/ShiftFrameworkDocumentation/pull/4 (branch `contributing-guide`, from master). Setup next to ShiftTemplates, DocPageMeta (nav + search), snippets, Docs.Verify scripts, status + TestedAgainst, pre-PR checklist. Every fact checked against master; docs solution builds with 0 errors and only CONTRIBUTING.md changes.
- This log published on my ShiftTemplates fork, branch `internship-log` (TestProject/ is git-ignored, so it isn't on any PR branch). Added the note on why the Day 3/4 work slipped (end of Week 1).
- **Data-level access re-verified on 2026.10.15.1**: https://github.com/ShiftSoftware/ShiftFrameworkDocumentation/pull/5 (branch `data-level-access-reviewed`). Local mode passed first time. `--packages` mode was broken: it built StockPlusPlus in place without `shiftFrameworkDevelopmentMode`, but since ShiftTemplates 606981a (2026-10-12) App.razor has a dev-only block written as a template condition (`@*#if*@`) that a C# constant can't remove → CS0103 AdmissionContext. Rewrote it to install that template version into a private hive, generate a project (sample + tests) and run the same test there; passes, cleans up (DB, folder, hive). Mutation: removing IntermediaryCompanyID from VehicleRepository makes the test fail (1 row instead of 4). Every claim checked against ShiftEntity master; the data-level code is unchanged since 2026.09.08.3. I first "fixed" one sentence (IDs parsed to the entity key type) and reverted it: Key/Keys only take `long?` columns, so the original was right. Only TestedAgainst changed; search index: only this page's version. Landing page: Docs #5 card, next step replaced (menu-link friction item).
- Landing page (commit fb1ea91): Docs #4 card + Docs I wrote entry, "Ten pull requests", links to this log (PR section, timeline, footer), timeline entry for 20 October.
- Landing page repo history rewritten (force-push, 2026-10-20): all three commits now hold only the cleaned images and video; the final files are unchanged. New hashes b3d8cdd, 14afcbc, 09144ed.

### Still to do
- Landing page: "Incidents I reproduced" (A, B, C with red → green test output and commit links) — after the Week 3 fixes below.
- Week 3 feedback: Incident A (keyset pagination + naive-loop test, save batches with CancellationToken.None, only permanent SQL errors mark Failed + short reasons, deterministic cancel tests via interceptor, template exclusion for Data/Sync, remove naive endpoint), Incident B (test 2 → real child-driven sweep, test 4 assertions, narrow the "different fields" claim, comments), Incident C (trap test comment + stamp assert, Lines lookup by Description, redundant line 67, comments at 34/56, standalone INCIDENT-C.md).
