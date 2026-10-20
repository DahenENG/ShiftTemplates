using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using StockPlusPlus.Data.DbContext;
using StockPlusPlus.Data.Entities;
using StockPlusPlus.Data.Repositories;
using StockPlusPlus.Shared.DTOs;
using StockPlusPlus.Shared.DTOs.Invoice;
using StockPlusPlus.Shared.DTOs.Product;
using System.Net;
using System.Net.Http.Json;

namespace StockPlusPlus.Test.Tests;

/// <summary>
/// DIAGNOSIS TESTS — Incident B, "the silent overwrite". Each test PASSES BY DEMONSTRATING A FLAW in how the framework
/// detects edit conflicts. They document current behaviour; they are not a specification. When a weakness is fixed,
/// its test should FAIL — then rewrite it to assert the fixed behaviour.
/// <para>
/// How conflicts are detected today (ShiftEntityCrudHandler.PutAsync): load the row, compare its LastSaveDate with the
/// DTO's, answer 409 if they differ, otherwise map and save. On every update ShiftRepository.UpsertAsync re-stamps the
/// ROOT's LastSaveDate, and no column is configured as an EF concurrency token, so the UPDATE itself checks nothing.
/// </para>
/// Runs against real SQL Server (localhost\sqlexpress, the test factory's database), not the in-memory provider.
/// </summary>
[Collection("API Collection")]
[Trait("Category", "Diagnosis")]
public class ConcurrencyDiagnosisTests
{
    private readonly CustomWebApplicationFactory factory;

    public ConcurrencyDiagnosisTests(CustomWebApplicationFactory factory)
    {
        this.factory = factory;
    }

    // ─────────────── Weakness 1 ───────────────

    /// <summary>
    /// A save that changes nothing still moves LastSaveDate (UpsertAsync stamps the root unconditionally), so every
    /// other tab holding the record now gets a 409 for a real edit — although no data changed.
    /// </summary>
    [Fact]
    public async Task Diagnosis1_ASaveThatChangesNothing_MovesTheStamp_SoEveryOtherOpenTabGets409()
    {
        using var client = factory.CreateClient();
        var created = await Created<CountryDTO>(await client.PostAsJsonAsync("api/country", new CountryDTO { Name = Unique("Country") }, Ct));

        var tabA = await Get<CountryDTO>(client, $"api/country/{created.ID}");
        var tabB = await Get<CountryDTO>(client, $"api/country/{created.ID}");

        // Tab A presses Save without changing anything.
        var afterNoOp = await Ok<CountryDTO>(await client.PutAsJsonAsync($"api/country/{created.ID}", tabA, Ct));
        Assert.Equal(tabA.Name, afterNoOp.Name);                    // no data changed...
        Assert.True(afterNoOp.LastSaveDate > tabA.LastSaveDate);   // ...but the stamp moved

        // Tab B makes a real edit and is refused.
        tabB.Name = "Edited in tab B";
        using var conflict = await client.PutAsJsonAsync($"api/country/{created.ID}", tabB, Ct);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var stored = await Get<CountryDTO>(client, $"api/country/{created.ID}");
        Assert.Equal(tabA.Name, stored.Name);
    }

    /// <summary>
    /// Weakness 1 again, on a parent with children: a tab that reloads the invoice and saves it with no edit at all
    /// moves the invoice's stamp, so another tab's header edit gets a 409. (The first version of Diagnosis2 changed a
    /// line through this same PUT and still passed with the line change deleted: it was showing this, not weakness 2.)
    /// </summary>
    [Fact]
    public async Task Diagnosis1b_AnInvoiceSavedWithNoEdit_AlsoMovesTheStamp_SoAHeaderEditElsewhereGets409()
    {
        using var client = factory.CreateClient();
        var invoiceId = await CreateInvoiceAsync(client, lineCount: 2);

        var tabA = await Get<InvoiceDTO>(client, $"api/Invoice/{invoiceId}");
        var tabB = await Get<InvoiceDTO>(client, $"api/Invoice/{invoiceId}");

        // Tab A saves exactly what it loaded.
        var afterNoOp = await Ok<InvoiceDTO>(await client.PutAsJsonAsync($"api/Invoice/{invoiceId}", tabA, Ct));
        Assert.Equal(tabA.ManualReference, afterNoOp.ManualReference);
        Assert.True(afterNoOp.LastSaveDate > tabB.LastSaveDate);

        tabB.ManualReference = "Header edit in tab B";
        using var conflict = await client.PutAsJsonAsync($"api/Invoice/{invoiceId}", tabB, Ct);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    // ─────────────── Weakness 2 ───────────────

    /// <summary>
    /// A child save that also changes its parent moves the PARENT's stamp. Here a line-level action (no PUT, no
    /// UpsertAsync) changes one line's price and recalculates the invoice's stored LinesTotal, then saves through the
    /// repository. The repository's save sweep stamps every Modified auditable row, so the invoice is re-stamped too,
    /// and another tab editing only the header gets a 409, although nobody touched the header.
    /// <para>
    /// The cause is the parent becoming Modified, not the child save itself: see the control test below, where the same
    /// action without the total leaves the invoice's stamp alone and the header edit succeeds.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Diagnosis2_AChildSaveThatRecalculatesTheParentTotal_MovesTheParentsStamp_SoAHeaderEditElsewhereGets409()
    {
        using var client = factory.CreateClient();
        var invoiceId = await CreateInvoiceAsync(client, lineCount: 2);
        var tabB = await Get<InvoiceDTO>(client, $"api/Invoice/{invoiceId}");

        var stamps = await ChangeLineThroughRepositoryAsync(invoiceId, price: 15m, recalculateTotal: true);

        Assert.True(stamps.LineAfter > stamps.LineBefore);      // the line was stamped by the sweep
        Assert.True(stamps.InvoiceAfter > stamps.InvoiceBefore); // ...and so was the invoice, because its total changed
        Assert.Equal(tabB.LastSaveDate, stamps.InvoiceBefore);

        tabB.ManualReference = "Header edit in tab B";
        using var conflict = await client.PutAsJsonAsync($"api/Invoice/{invoiceId}", tabB, Ct);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    /// <summary>
    /// Control for Diagnosis2: the same line-level action without recalculating the total. Only the line is Modified,
    /// so the sweep stamps only the line, the invoice keeps its stamp, and the other tab's header edit is accepted.
    /// (That accepted PUT also sends tab B's older line back: weakness 3, see Diagnosis3.)
    /// </summary>
    [Fact]
    public async Task Diagnosis2_Control_AChildOnlySave_LeavesTheParentsStampAlone()
    {
        using var client = factory.CreateClient();
        var invoiceId = await CreateInvoiceAsync(client, lineCount: 2);
        var tabB = await Get<InvoiceDTO>(client, $"api/Invoice/{invoiceId}");

        var stamps = await ChangeLineThroughRepositoryAsync(invoiceId, price: 15m, recalculateTotal: false);

        Assert.True(stamps.LineAfter > stamps.LineBefore);
        Assert.Equal(stamps.InvoiceBefore, stamps.InvoiceAfter);

        tabB.ManualReference = "Header edit in tab B";
        using var accepted = await client.PutAsJsonAsync($"api/Invoice/{invoiceId}", tabB, Ct);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    // ─────────────── Weakness 3 ───────────────

    /// <summary>
    /// Child DTOs carry their own LastSaveDate, but PutAsync compares only the root's. A line changed by another path
    /// (a line-level action that stamps the line, not the invoice) is silently overwritten by a tab holding the older
    /// line: no 409, because the invoice's own stamp never changed.
    /// </summary>
    [Fact]
    public async Task Diagnosis3_VersionStampsOnChildDtos_AreNeverCompared_SoAStaleLineSilentlyOverwritesANewerOne()
    {
        using var client = factory.CreateClient();
        var invoiceId = await CreateInvoiceAsync(client, lineCount: 1);

        var tabA = await Get<InvoiceDTO>(client, $"api/Invoice/{invoiceId}");
        var staleLine = tabA.InvoiceLines.Single();
        Assert.Equal(10m, staleLine.Price);

        // Another path changes the line and stamps the LINE (as a line-level save does), leaving the invoice alone.
        // Simulated: ChangeLineAsync writes through the bare DbContext and sets the line's stamp by hand, because a
        // bare-context save does not run the repository's stamping sweep.
        var newerLineStamp = await ChangeLineAsync(invoiceId, price: 99m);
        Assert.NotEqual(staleLine.LastSaveDate, newerLineStamp);

        // Tab A edits only the header and saves, sending its stale line (price 10, old stamp) along.
        tabA.ManualReference = "Header edit in tab A";
        using var response = await client.PutAsJsonAsync($"api/Invoice/{invoiceId}", tabA, Ct);

        // This 200 is the proof: the line tab A sent is older than the stored one, but only the root's stamp is
        // compared, so the stale child goes through without a conflict.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The newer line value is gone.
        Assert.Equal([10m], await LinePricesAsync(invoiceId));
    }

    // ─────────────── Weakness 4 ───────────────

    /// <summary>
    /// The conflict check and the save are separate steps, and the UPDATE checks no version. Two saves that load the
    /// same version both pass the check and both commit, and both callers are told "saved": when they changed the same
    /// field, the earlier edit is silently lost. Driven through the repository to interleave the two requests
    /// deterministically, following PutAsync's steps: FindAsync, compare LastSaveDate, UpsertAsync, SaveChangesAsync.
    /// <para>
    /// Observed while writing this, for scalar columns of this entity: when the two saves change DIFFERENT columns, both
    /// survive, because EF Core's UPDATE sets only the columns that changed on that request's tracked row (see the
    /// Observation test below). That does not hold for child collections, which repositories such as InvoiceRepository
    /// replace on save, or for members derived from other data. The race loses data when the edits overlap.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Diagnosis4_TwoSavesThatBothPassTheConflictCheck_BothCommit_AndOneEditIsSilentlyLost()
    {
        var brandId = await CreateBrandAsync(code: "CODE-0", description: "Description 0");

        using var requestA = factory.Services.CreateScope();
        using var requestB = factory.Services.CreateScope();
        var repoA = requestA.ServiceProvider.GetRequiredService<ProductBrandRepository>();
        var repoB = requestB.ServiceProvider.GetRequiredService<ProductBrandRepository>();

        // Both requests load the row and pass the conflict check against the same version.
        var rowA = (await repoA.FindAsync(brandId, null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true))!;
        var rowB = (await repoB.FindAsync(brandId, null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true))!;
        var dtoA = await repoA.ViewAsync(rowA);
        var dtoB = await repoB.ViewAsync(rowB);
        Assert.Equal(rowA.LastSaveDate, dtoA.LastSaveDate); // PutAsync's check passes for A
        Assert.Equal(rowB.LastSaveDate, dtoB.LastSaveDate); // ...and for B

        // A changes the code and commits.
        dtoA.Code = "CODE-A";
        await repoA.UpsertAsync(rowA, dtoA, ActionTypes.Update, null, null, true, true);
        await repoA.SaveChangesAsync();

        // Prove A's edit really was committed, and keep the stamp it produced.
        var afterA = await ReadBrandAsync(brandId);
        Assert.Equal("CODE-A", afterA.Code);
        var stampAfterA = afterA.LastSaveDate;
        Assert.NotEqual(stampAfterA, dtoB.LastSaveDate); // B now holds a stale version

        // B, still holding the version A just replaced, changes the same field and commits too: no exception, the stale
        // version is not detected.
        dtoB.Code = "CODE-B";
        await repoB.UpsertAsync(rowB, dtoB, ActionTypes.Update, null, null, true, true);
        await repoB.SaveChangesAsync();

        var stored = await ReadBrandAsync(brandId);
        Assert.Equal("CODE-B", stored.Code);              // A was told "saved", but its committed edit is gone
        Assert.True(stored.LastSaveDate > stampAfterA);   // B's save landed on top of A's
    }

    /// <summary>
    /// Not a weakness, the boundary of weakness 4: two saves from the same version that change DIFFERENT scalar columns
    /// of ProductBrand both survive, because EF writes only the columns that changed on each tracked row.
    /// </summary>
    [Fact]
    public async Task Observation_TwoSavesFromTheSameVersion_ThatChangeDifferentScalarColumns_BothSurvive()
    {
        var brandId = await CreateBrandAsync(code: "CODE-0", description: "Description 0");

        using var requestA = factory.Services.CreateScope();
        using var requestB = factory.Services.CreateScope();
        var repoA = requestA.ServiceProvider.GetRequiredService<ProductBrandRepository>();
        var repoB = requestB.ServiceProvider.GetRequiredService<ProductBrandRepository>();

        var rowA = (await repoA.FindAsync(brandId, null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true))!;
        var rowB = (await repoB.FindAsync(brandId, null, disableDefaultDataLevelAccess: true, disableGlobalFilters: true))!;
        var dtoA = await repoA.ViewAsync(rowA);
        var dtoB = await repoB.ViewAsync(rowB);

        dtoA.Code = "CODE-A";
        await repoA.UpsertAsync(rowA, dtoA, ActionTypes.Update, null, null, true, true);
        await repoA.SaveChangesAsync();
        Assert.Equal("CODE-A", (await ReadBrandAsync(brandId)).Code);

        dtoB.Description = "Description B";
        await repoB.UpsertAsync(rowB, dtoB, ActionTypes.Update, null, null, true, true);
        await repoB.SaveChangesAsync();

        var stored = await ReadBrandAsync(brandId);
        Assert.Equal("CODE-A", stored.Code);
        Assert.Equal("Description B", stored.Description);
    }

    // ─────────────── helpers ───────────────

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix} {Guid.NewGuid():N}"[..(prefix.Length + 13)];

    private static async Task<T> Get<T>(HttpClient client, string url)
        => await Ok<T>(await client.GetAsync(url, Ct));

    private static async Task<T> Ok<T>(HttpResponseMessage response) => await Entity<T>(response, HttpStatusCode.OK);

    private static async Task<T> Created<T>(HttpResponseMessage response) => await Entity<T>(response, HttpStatusCode.Created);

    private static async Task<T> Entity<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == expected, $"Expected {(int)expected}, got {(int)response.StatusCode}: {body}");
            return JsonSerializer.Deserialize<ShiftEntityResponse<T>>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Entity!;
        }
    }

    // An invoice with lines priced 10, 20, … on a product created for the test.
    private async Task<string> CreateInvoiceAsync(HttpClient client, int lineCount)
    {
        var product = await CreateProductAsync();
        var invoice = new InvoiceDTO
        {
            ManualReference = Unique("REF"),
            InvoiceDate = DateTimeOffset.UtcNow,
            InvoiceNo = Random.Shared.NextInt64(1_000_000, long.MaxValue),
            InvoiceLines = Enumerable.Range(1, lineCount)
                .Select(i => new InvoiceLineDTO { Product = product, Description = $"Line {i}", Price = 10 * i })
                .ToList(),
        };

        var key = (await Created<InvoiceDTO>(await client.PostAsJsonAsync("api/Invoice", invoice, Ct))).ID!;

        // Start with the correct stored total (LinesTotal is not on the DTOs). ExecuteUpdate bypasses the change
        // tracker, so no stamp moves; a later recalculation then changes the invoice only if a line price changed.
        using var scope = factory.Services.CreateScope();
        var invoiceId = scope.ServiceProvider.GetRequiredService<IHashIdService>().Decode<InvoiceDTO>(key);
        var total = invoice.InvoiceLines.Sum(x => x.Price);
        await scope.ServiceProvider.GetRequiredService<DB>().Invoices
            .Where(x => x.ID == invoiceId)
            .ExecuteUpdateAsync(x => x.SetProperty(i => i.LinesTotal, (decimal?)total), Ct);

        return key;
    }

    private async Task<ShiftEntitySelectDTO> CreateProductAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DB>();

        var brand = new ProductBrand { Name = Unique("Brand") };
        var category = new ProductCategory { Name = Unique("Category") };
        var product = new Product { Name = Unique("Product"), ProductBrand = brand, ProductCategory = category };
        db.AddRange(brand, category, product);
        await db.SaveChangesAsync(Ct);

        var hashIds = scope.ServiceProvider.GetRequiredService<IHashIdService>();
        return new ShiftEntitySelectDTO { Value = hashIds.Encode<ProductDTO>(product.ID) };
    }

    private async Task<long> CreateBrandAsync(string code, string description)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DB>();
        var brand = new ProductBrand { Name = Unique("Brand"), Code = code, Description = description };
        db.Add(brand);
        await db.SaveChangesAsync(Ct);
        return brand.ID;
    }

    private async Task<ProductBrand> ReadBrandAsync(long brandId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DB>().Brands.AsNoTracking().SingleAsync(x => x.ID == brandId, Ct);
    }

    // A line-level action done properly: one request scope, the line changed on the tracked graph, saved through the
    // repository (so the framework's sweep does the stamping), with no PUT and no UpsertAsync. Optionally recalculates
    // the invoice's stored total, which makes the invoice Modified too. Stamps are read back in a fresh scope.
    private async Task<(DateTimeOffset InvoiceBefore, DateTimeOffset InvoiceAfter, DateTimeOffset LineBefore, DateTimeOffset LineAfter)>
        ChangeLineThroughRepositoryAsync(string invoiceKey, decimal price, bool recalculateTotal)
    {
        long invoiceId, lineId;
        DateTimeOffset invoiceBefore, lineBefore;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DB>();
            var repository = scope.ServiceProvider.GetRequiredService<InvoiceRepository>();
            invoiceId = scope.ServiceProvider.GetRequiredService<IHashIdService>().Decode<InvoiceDTO>(invoiceKey);

            var invoice = await db.Invoices.Include(x => x.InvoiceLines).SingleAsync(x => x.ID == invoiceId, Ct);
            var line = invoice.InvoiceLines.OrderBy(x => x.ID).First();
            (lineId, invoiceBefore, lineBefore) = (line.ID, invoice.LastSaveDate, line.LastSaveDate);

            line.Price = price;
            if (recalculateTotal)
                invoice.LinesTotal = invoice.InvoiceLines.Sum(x => x.Price);

            await repository.SaveChangesAsync();
        }

        using var check = factory.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<DB>();
        var invoiceAfter = await checkDb.Invoices.AsNoTracking().Where(x => x.ID == invoiceId).Select(x => x.LastSaveDate).SingleAsync(Ct);
        var lineAfter = await checkDb.InvoiceLines.AsNoTracking().Where(x => x.ID == lineId).Select(x => x.LastSaveDate).SingleAsync(Ct);

        return (invoiceBefore, invoiceAfter, lineBefore, lineAfter);
    }

    // Change the invoice's line the way a line-level action would: new value, new LINE stamp, invoice untouched.
    private async Task<DateTimeOffset> ChangeLineAsync(string invoiceKey, decimal price)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DB>();
        var invoiceId = scope.ServiceProvider.GetRequiredService<IHashIdService>().Decode<InvoiceDTO>(invoiceKey);

        var line = await db.InvoiceLines.SingleAsync(x => x.InvoiceID == invoiceId, Ct);
        line.Price = price;
        line.LastSaveDate = DateTimeOffset.UtcNow; // a bare-context update does not stamp modified rows itself
        await db.SaveChangesAsync(Ct);
        return line.LastSaveDate;
    }

    private async Task<List<decimal>> LinePricesAsync(string invoiceKey)
    {
        using var scope = factory.Services.CreateScope();
        var invoiceId = scope.ServiceProvider.GetRequiredService<IHashIdService>().Decode<InvoiceDTO>(invoiceKey);
        return await scope.ServiceProvider.GetRequiredService<DB>().InvoiceLines
            .Where(x => x.InvoiceID == invoiceId).Select(x => x.Price).ToListAsync(Ct);
    }
}
