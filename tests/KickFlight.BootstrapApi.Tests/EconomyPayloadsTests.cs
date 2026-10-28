using System.Text.Json;
using KickFlight.BootstrapApi.Otomo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The payload builders, from an economy state written by hand. These are the shapes the patched client
// deserializes, so what is asserted here is the field names and the mapping, not the HTTP path - that is
// OtomoEconomyTests' and EconomyEndToEndTests' job.
public sealed class EconomyPayloadsTests
{
    private static readonly Dictionary<int, int> CostumeKicker = new()
    {
        [2010101] = 1,
        [2010201] = 1,
        [2020101] = 2
    };

    private static readonly int[] KnownDiscs = [3010001, 3010002];

    private static EconomyPayloads Build() =>
        new(CostumeKicker, _ => new[] { 0, 0, 0 }, KnownDiscs, NullLogger.Instance);

    private static JsonElement Payload(object value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }

    private static EconomySnapshot Snapshot((string Item, long Qty)[]? items = null,
        IReadOnlyList<EconomyOffer>? offers = null, IReadOnlyList<EconomyPool>? pools = null) =>
        new([], (items ?? []).Select(item => new EconomyItem(item.Item, item.Qty, true, null, null)).ToList(),
            offers ?? [], pools ?? []);

    [Fact]
    public void Item_list_carries_every_master_row_and_maps_currencies_and_items_by_name()
    {
        var snapshot = new EconomySnapshot(
            [
                new EconomyBalance("jetcoin", 250),
                new EconomyBalance("jetcoin.paid", 7),
                new EconomyBalance("discforce", 1100),
                new EconomyBalance("kickpoint", 900)
            ],
            [
                new EconomyItem("stamp.gear", 12, true, null, null),
                new EconomyItem("ticket.kicker", 3, true, null, null),
                // Neither of these is a counter on this screen, and one is not a name the adapter knows at all.
                new EconomyItem("skin:2010101", 1, false, null, null),
                new EconomyItem("mystery.item", 5, true, null, null)
            ],
            [], []);

        var rows = Payload(Build().BuildUserItemList(snapshot)).EnumerateArray().ToList();

        Assert.Equal(8, rows.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, rows.Select(row => row.GetProperty("itemId").GetInt32()).ToArray());
        Assert.Equal(250, Amount(rows, 1));
        Assert.Equal(7, Amount(rows, 2));
        Assert.Equal(1100, Amount(rows, 3));
        Assert.Equal(900, Amount(rows, 4));
        Assert.Equal(12, Amount(rows, 5));
        Assert.Equal(0, Amount(rows, 6));
        Assert.Equal(3, Amount(rows, 7));
        Assert.Equal(0, Amount(rows, 8));
        Assert.All(rows, row => Assert.Equal(new[] { "itemId", "amount" }, row.EnumerateObject().Select(p => p.Name).ToArray()));
    }

    [Fact]
    public void A_currency_the_adapter_has_no_row_for_is_left_out_rather_than_guessed()
    {
        var snapshot = new EconomySnapshot(
            [new EconomyBalance("shard", 4000), new EconomyBalance("jetcoin", 1)], [], [], []);

        var rows = Payload(Build().BuildUserItemList(snapshot)).EnumerateArray().ToList();

        Assert.Equal(8, rows.Count);
        Assert.Equal(1, Amount(rows, 1));
        Assert.Equal(0, rows.Skip(1).Sum(row => row.GetProperty("amount").GetInt32()));
    }

    [Fact]
    public void Disc_list_serves_owned_discs_at_level_ten_and_drops_ids_the_client_has_no_master_row_for()
    {
        var snapshot = Snapshot(items:
        [
            ("disc:3010001", 2),
            ("disc:3010002", 1),
            ("disc:9999999", 1)
        ]);

        var rows = Payload(Build().BuildUserDiscList(snapshot)).EnumerateArray().ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(3010001, rows[0].GetProperty("discId").GetInt32());
        Assert.Equal(2, rows[0].GetProperty("amount").GetInt32());
        Assert.Equal(10, rows[0].GetProperty("level").GetInt32());
        Assert.Equal(0, rows[0].GetProperty("exp").GetInt32());
        Assert.Equal(new[] { "discId", "amount", "level", "exp", "registerDatetime" },
            rows[0].EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(3010002, rows[1].GetProperty("discId").GetInt32());
    }

    [Fact]
    public void Kicker_list_comes_from_owned_costumes_and_always_keeps_the_active_one()
    {
        // The player owns costume 2010102 of kicker 1 only; the save says they are playing kicker 2's costume.
        var snapshot = Snapshot(items: [("skin:2010201", 1)]);

        var rows = Payload(Build().BuildUserKickerList(snapshot, activeKickerId: 2, activeCostumeId: 2020101))
            .EnumerateArray().ToList();

        Assert.Equal(2, rows.Count);
        var kicker1 = rows.Single(row => row.GetProperty("kickerId").GetInt32() == 1);
        var kicker2 = rows.Single(row => row.GetProperty("kickerId").GetInt32() == 2);
        // Kicker 1 is not the active one, so it shows its first owned costume; both it owns are listed.
        Assert.Equal(2010101, kicker1.GetProperty("kickerCostumeId").GetInt32());
        Assert.Equal(new[] { 2010101, 2010201 }, kicker1.GetProperty("userKickerCostumeList").EnumerateArray()
            .Select(costume => costume.GetProperty("kickerCostumeId").GetInt32()).ToArray());
        // The selection is what the client draws the player with, so it is served even with nothing owned.
        Assert.Equal(2020101, kicker2.GetProperty("kickerCostumeId").GetInt32());
        var costume = kicker2.GetProperty("userKickerCostumeList")[0];
        Assert.Equal(new[] { "kickerCostumeId", "gearId1", "gearId2", "gearId3" },
            costume.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Fact]
    public void A_draw_maps_to_a_costume_row_and_its_kicker_and_the_receipt_lists_the_owned_kickers()
    {
        var draws = new[]
        {
            new EconomyDraw("skin:2010101", "common", false, null, 0, false),
            new EconomyDraw("skin:2010101", "common", true, "chip", 5, false)
        };
        var after = Snapshot(items: [("skin:2010101", 2)]);

        var drops = Payload(Build().BuildDropKickerCostumes(draws)).EnumerateArray().ToList();
        var receipts = Payload(Build().BuildReceivedUserKickerList(draws, after, 1, 2010101)).EnumerateArray().ToList();

        Assert.Equal(2, drops.Count);
        Assert.Equal(1, drops[0].GetProperty("kickerId").GetInt32());
        Assert.Equal(2010101, drops[0].GetProperty("kickerCostumeId").GetInt32());
        Assert.Single(receipts);
        Assert.Equal(1, receipts[0].GetProperty("kickerId").GetInt32());
    }

    [Fact]
    public void Received_items_report_the_total_economy_returned_not_a_number_this_process_computed()
    {
        var moves = new[]
        {
            new EconomyItemMove("stamp.gear", 10, 25),
            new EconomyItemMove("skin:2010101", 1, 1),
            new EconomyItemMove("mystery.item", 1, 1)
        };

        var rows = Payload(Build().BuildReceivedUserItemList(moves)).EnumerateArray().ToList();

        var only = Assert.Single(rows);
        Assert.Equal(5, only.GetProperty("itemId").GetInt32());
        Assert.Equal(25, only.GetProperty("amount").GetInt32());
    }

    private static int Amount(List<JsonElement> rows, int itemId) =>
        rows.Single(row => row.GetProperty("itemId").GetInt32() == itemId).GetProperty("amount").GetInt32();
}
