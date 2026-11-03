using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KickFlight.BootstrapApi.Otomo;

namespace KickFlight.BootstrapApi;

// The shop screen's two gachas, served by otomo's economy.
//
// Both tabs are pools. Economy decides what a draw costs, what it can drop and how a duplicate is handled, and the
// adapter asks for one roll per accepted request (POST /api/player/economy/gacha/roll) with the player's own token.
// It holds no drop table and no balance of its own: "you cannot afford this" is economy's refusal, not a number this
// process keeps.
//
//   kicker tab  GachaKicker row 1 -> cosmetic.standard, one draw per press, paid in kicker_ticket
//   disc tab    DiscGacha row 1   -> disc.standard, one draw for its cost (300 jetcoin)
//               DiscGacha row 2   -> disc.eleven, a bundle of eleven draws for one cost (3000 jetcoin)
//
// A draw request carries nothing but gachaId (GachaDiscDrawRequestData / GachaKickerDrawRequestData), so every
// button is its own ResponseGacha row, with dropAmount saying how many discs it gives. The price shown is the pool's.
//
// Retention point for the ShopDiscScroller crash (round C, item 4) is unchanged: HomeResponseData.discGachaGroupList
// and kickerGachaGroupList feed GachaGroupListInfo -> ShopDisplayView -> ShopDiscScroller.Initialize, whose
// GetCellHeight(int) indexes _data[dataIndex]. Both lists must carry a group and a non-empty gachaList.
public sealed partial class DemoSessionApi
{
    // ResponseGachaGroup dates and the "unlimited" drawable count a served gacha used to carry.
    public const string GachaStartDatetime = "2019-01-01 00:00:00";
    public const string GachaEndDatetime = "2030-01-01 23:59:59";
    private const int GachaDrawableCount = 9999;

    // The GachaGroup master rows: 1 is the disc tab, 2 the kicker tab.
    private const int DiscGachaGroupId = 1;
    private const int KickerGachaGroupId = 2;

    // Economy's MaxRollCount: one roll moves at most eleven draws (a ten-plus-one bundle), so the adapter does not
    // ask for more.
    private const int MaxRollCount = 11;

    private sealed record GachaGroupRow(int Id, string Name, int GachaType, string BannerImageFilename,
        string StartDatetime, string EndDatetime, string InformationText, string NoticeText);

    private sealed record DiscGachaRow(int Id, int GachaGroupId, int LotteryId, int RarityGroupId,
        int DropAmountGroupId, int LotteryCostId, int LotteryCostAmount, int ModelId,
        string EffectTopImageFilename, string EffectBottomImageFilename);

    private sealed record KickerGachaRow(int Id, int GachaGroupId, int LotteryId, int LotteryCostId,
        int LotteryCostAmount, bool BoxFlag, bool SideBySideFlag, string EffectTopImageFilename,
        string EffectBottomImageFilename);

    private sealed record LotteryCostRow(int Id, int GoodsType, int GoodsId);

    private sealed record LotteryDiscRarityRow(int GroupId, int RarityType, double DropRate, bool PickupFlag);

    private sealed record LotteryDropDiscRow(int DiscId, int RarityType, double DropRate, int SortOrder);

    private sealed record LotteryDropKickerRow(int KickerCostumeId, double DropRate, int SortOrder);

    private readonly Dictionary<int, GachaGroupRow> _gachaGroupById = [];
    private readonly Dictionary<int, DiscGachaRow> _discGachaById = [];
    private readonly Dictionary<int, KickerGachaRow> _kickerGachaById = [];
    private readonly Dictionary<int, LotteryCostRow> _lotteryCostById = [];
    private readonly Dictionary<int, List<LotteryDiscRarityRow>> _lotteryDiscRarityByGroup = [];
    private readonly List<LotteryDropDiscRow> _lotteryDropDisc = [];
    private readonly List<LotteryDropKickerRow> _lotteryDropKicker = [];

    // Every table is announced to the client like Gear/Capsule: one encrypted blob per name plus a row in the
    // /download/master list, which BuildDownloadMasterJson derives from _encryptedMasters.
    private void InitializeGachaMasters(string contentRoot)
    {
        var groupJson = LoadJson(contentRoot, "config/masters_gacha_group.json", "[]");
        var discGachaJson = LoadJson(contentRoot, "config/masters_disc_gacha.json", "[]");
        var kickerGachaJson = LoadJson(contentRoot, "config/masters_kicker_gacha.json", "[]");
        var lotteryCostJson = LoadJson(contentRoot, "config/masters_lottery_cost.json", "[]");
        var lotteryDiscRarityJson = LoadJson(contentRoot, "config/masters_lottery_disc_rarity.json", "[]");
        var lotteryDropDiscJson = LoadJson(contentRoot, "config/masters_lottery_drop_disc.json", "[]");
        var lotteryDropKickerJson = LoadJson(contentRoot, "config/masters_lottery_drop_kicker.json", "[]");
        var lotteryDiscDropAmountJson = LoadJson(contentRoot, "config/masters_lottery_disc_drop_amount.json", "[]");
        var lotteryDropCountRewardJson = LoadJson(contentRoot, "config/masters_lottery_drop_count_reward.json", "[]");
        var lotteryGearRarityJson = LoadJson(contentRoot, "config/masters_lottery_gear_rarity.json", "[]");

        _encryptedMasters["GachaGroup"] = EncryptMaster(groupJson);
        _encryptedMasters["DiscGacha"] = EncryptMaster(discGachaJson);
        _encryptedMasters["KickerGacha"] = EncryptMaster(kickerGachaJson);
        _encryptedMasters["LotteryCost"] = EncryptMaster(lotteryCostJson);
        _encryptedMasters["LotteryDiscRarity"] = EncryptMaster(lotteryDiscRarityJson);
        _encryptedMasters["LotteryDropDisc"] = EncryptMaster(lotteryDropDiscJson);
        _encryptedMasters["LotteryDropKicker"] = EncryptMaster(lotteryDropKickerJson);
        _encryptedMasters["LotteryDiscDropAmount"] = EncryptMaster(lotteryDiscDropAmountJson);
        _encryptedMasters["LotteryDropCountReward"] = EncryptMaster(lotteryDropCountRewardJson);
        _encryptedMasters["LotteryGearRarity"] = EncryptMaster(lotteryGearRarityJson);

        try
        {
            foreach (var el in JsonDocument.Parse(groupJson).RootElement.EnumerateArray())
            {
                var row = new GachaGroupRow(IntOf(el, "id"), StrOf(el, "name"), IntOf(el, "gachaType"),
                    StrOf(el, "bannerImageFilename"), StrOf(el, "startDatetime"), StrOf(el, "endDatetime"),
                    StrOf(el, "informationText"), StrOf(el, "noticeText"));
                _gachaGroupById[row.Id] = row;
            }

            foreach (var el in JsonDocument.Parse(discGachaJson).RootElement.EnumerateArray())
            {
                var row = new DiscGachaRow(IntOf(el, "id"), IntOf(el, "gachaGroupId"), IntOf(el, "lotteryId"),
                    IntOf(el, "lotteryDiscRarityGroupId"), IntOf(el, "lotteryDiscDropAmountGroupId"),
                    IntOf(el, "lotteryCostId"), IntOf(el, "lotteryCostAmount"), IntOf(el, "modelId"),
                    StrOf(el, "effectTopImageFilename"), StrOf(el, "effectBottomImageFilename"));
                _discGachaById[row.Id] = row;
            }

            foreach (var el in JsonDocument.Parse(kickerGachaJson).RootElement.EnumerateArray())
            {
                var row = new KickerGachaRow(IntOf(el, "id"), IntOf(el, "gachaGroupId"), IntOf(el, "lotteryId"),
                    IntOf(el, "lotteryCostId"), IntOf(el, "lotteryCostAmount"), BoolOf(el, "boxFlag"),
                    BoolOf(el, "sideBySideFlag"), StrOf(el, "effectTopImageFilename"), StrOf(el, "effectBottomImageFilename"));
                _kickerGachaById[row.Id] = row;
            }

            foreach (var el in JsonDocument.Parse(lotteryCostJson).RootElement.EnumerateArray())
            {
                // The pull button draws its cost icon from "ui/goods/thumbnail/<goodsId>", so the id is the
                // composite the asset list carries (4010001 for a kicker ticket, 4020001 for a disc ticket),
                // not the raw Item row the master file names. The raw id is kept as the spend side.
                var costType = IntOf(el, "goodsType");
                var row = new LotteryCostRow(IntOf(el, "id"), costType, costType * 10000 + 1);
                _lotteryCostById[row.Id] = row;
            }

            foreach (var el in JsonDocument.Parse(lotteryDiscRarityJson).RootElement.EnumerateArray())
            {
                var row = new LotteryDiscRarityRow(IntOf(el, "groupId"), IntOf(el, "rarityType"),
                    DoubleOf(el, "dropRate"), BoolOf(el, "pickupFlag"));
                if (!_lotteryDiscRarityByGroup.TryGetValue(row.GroupId, out var list))
                {
                    list = [];
                    _lotteryDiscRarityByGroup[row.GroupId] = list;
                }
                list.Add(row);
            }

            foreach (var el in JsonDocument.Parse(lotteryDropDiscJson).RootElement.EnumerateArray())
            {
                _lotteryDropDisc.Add(new LotteryDropDiscRow(IntOf(el, "discId"), IntOf(el, "rarityType"),
                    DoubleOf(el, "dropRate"), IntOf(el, "sortOrder")));
            }

            foreach (var el in JsonDocument.Parse(lotteryDropKickerJson).RootElement.EnumerateArray())
            {
                _lotteryDropKicker.Add(new LotteryDropKickerRow(IntOf(el, "kickerCostumeId"),
                    DoubleOf(el, "dropRate"), IntOf(el, "sortOrder")));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error parsing gacha master json: {Error}", ex.Message);
        }

        if (_kickerGachaById.Count == 0)
        {
            _logger.LogWarning("Kicker gacha master is empty: the home screen will serve no cosmetic banner");
        }
        if (EconomyCatalog.KickerGachas.All(row => !_kickerGachaById.ContainsKey(row.GachaId)))
        {
            _logger.LogError(
                "No served KickerGacha row matches the economy pool mapping ({Mappings}); no kicker draw can be given a pool",
                string.Join(", ", EconomyCatalog.KickerGachas.Select(row => $"{row.GachaId}={row.PoolId}")));
        }
    }


    // The client does not use one key for every body. The session key (the ASCII "hash" the client chose at
    // login) is what most authenticated requests arrive with, but /gacha/kickerInformation on 2.11.5 arrives
    // encrypted with the common code, and decoding that with the session key throws
    // "Padding is invalid and cannot be removed" - which reached the client as x-app-status-code 1 and an empty
    // body, so the gacha information sheet never opened and the banner looked dead. Try the ticket's key first,
    // then the common code: a wrong key fails on the padding, so this cannot mis-read a body.
    private static byte[] DecodeEitherKey(byte[] body, byte[] key)
    {
        try
        {
            return D2CCodec.Decode(body, key);
        }
        catch (CryptographicException)
        {
            return D2CCodec.Decode(body, Encoding.ASCII.GetBytes(CommonCode));
        }
    }

    private async Task<IResult?> TryHandleGachaAsync(string path, HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
    {
        switch (path)
        {
            case "/gacha/read":
                // GachaReadResponseData declares no fields at all, so the neutral payload is the empty object.
                context.Response.Headers["x-app-status-code"] = "0";
                context.Response.Headers["x-kickflight-fixture"] = "dynamic-gacha-read";
                return BinaryJson("{}", key);

            case "/gacha/discInformation":
                return await HandleGachaDiscInformationAsync(context, key);

            case "/gacha/kickerInformation":
                return await HandleGachaKickerInformationAsync(context, ticket, key);

            case "/gacha/discDraw":
                return await HandleGachaDiscDrawAsync(context, ticket, state, key);

            case "/gacha/kickerDraw":
                return await HandleGachaKickerDrawAsync(context, ticket, state, key);

            default:
                return null;
        }
    }

    private async Task<IResult?> HandleGachaDiscInformationAsync(HttpContext context, byte[] key)
    {
        var body = await ReadBodyAsync(context.Request);
        var gachaGroupId = DiscGachaGroupId;
        try
        {
            if (body.Length > 0)
            {
                var plaintext = DecodeEitherKey(body, key);
                using var document = JsonDocument.Parse(plaintext);
                gachaGroupId = IntOf(document.RootElement, "gachaGroupId", DiscGachaGroupId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode gacha disc information request: {Error}", ex.Message);
            return StatusError(context, key);
        }

        // ResponseDiscGachaGroupInformation: one entry per DiscGacha row of the requested group. The rates are the
        // served lottery masters', which only drive this screen; the draw itself is the pool's.
        var groups = new List<object>();
        foreach (var gacha in _discGachaById.Values.Where(g => g.GachaGroupId == gachaGroupId).OrderBy(g => g.Id))
        {
            var rarities = _lotteryDiscRarityByGroup.TryGetValue(gacha.RarityGroupId, out var rList)
                ? rList.Select(r => new { rarityType = r.RarityType, pickupFlag = r.PickupFlag, dropRate = r.DropRate }).ToArray()
                : [];
            var drops = _lotteryDropDisc
                .OrderBy(d => d.SortOrder)
                .Select(d => new { discId = d.DiscId, pickupFlag = false, newFlag = false, dropRate = d.DropRate })
                .ToArray();
            groups.Add(new
            {
                lotteryId = gacha.LotteryId,
                name = _gachaGroupById.TryGetValue(gachaGroupId, out var group) ? group.Name : "",
                lotteryDiscRarityList = rarities,
                lotteryDropDiscList = drops
            });
        }

        var response = new
        {
            informationText = GachaGroupText(gachaGroupId, information: true),
            noticeText = GachaGroupText(gachaGroupId, information: false),
            discGachaGroupInformationList = groups.ToArray(),
            dropDiscForceList = Array.Empty<object>(),
            lotteryDropDiscForceList = Array.Empty<object>()
        };

        context.Response.Headers["x-app-status-code"] = "0";
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-gacha-information";
        return BinaryJson(JsonSerializer.Serialize(response), key);
    }

    private async Task<IResult?> HandleGachaKickerInformationAsync(HttpContext context, KfIdentityTicket ticket, byte[] key)
    {
        var body = await ReadBodyAsync(context.Request);
        var gachaGroupId = KickerGachaGroupId;
        try
        {
            if (body.Length > 0)
            {
                var plaintext = DecodeEitherKey(body, key);
                using var document = JsonDocument.Parse(plaintext);
                gachaGroupId = IntOf(document.RootElement, "gachaGroupId", KickerGachaGroupId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode gacha kicker information request: {Error}", ex.Message);
            return StatusError(context, key);
        }

        var gacha = _kickerGachaById.Values.FirstOrDefault(g => g.GachaGroupId == gachaGroupId);

        // The rate screen renders `dropRate` from this response and reads no master at all (docs/OTOMO_PHASE0_FINDINGS
        // 9.6): a percentage, 100 x weight / sum(weights), with a value <= 0 hiding that row. LotteryDropKicker's own
        // dropRatio is 1 on every row and the client never reads it, so serving the master here printed 0% on all
        // 118 rows. The weights the adapter actually rolls are economy's pool entries, so those are what it sends.
        var dropList = new List<object>();
        var economy = await ReadPlayerEconomyAsync(ticket, EconomyRead.Pools, "/gacha/kickerInformation",
            context.RequestAborted);
        var pool = economy.Snapshot?.Pools?.FirstOrDefault(p =>
            string.Equals(p.Id, EconomyCatalog.CosmeticPoolId, StringComparison.Ordinal));
        if (pool?.Entries is { Count: > 0 } entries)
        {
            var total = pool.TotalWeight > 0 ? pool.TotalWeight : entries.Sum(entry => entry.Weight);
            foreach (var entry in entries)
            {
                if (!EconomyCatalog.TryCostumeId(entry.Item, out var costumeId)) continue;
                dropList.Add(new
                {
                    kickerCostumeId = costumeId,
                    pickupFlag = false,
                    newFlag = false,
                    dropRate = total > 0 ? Math.Round(100.0 * entry.Weight / total, 3) : 0.0
                });
            }
        }
        if (dropList.Count == 0)
        {
            dropList.AddRange(_lotteryDropKicker.OrderBy(k => k.SortOrder)
                .Select(k => (object)new { kickerCostumeId = k.KickerCostumeId, pickupFlag = false, newFlag = false, dropRate = 0.0 }));
        }

        var response = new
        {
            informationText = GachaGroupText(gachaGroupId, information: true),
            noticeText = GachaGroupText(gachaGroupId, information: false),
            boxFlag = gacha?.BoxFlag ?? false,
            sideBySideFlag = gacha?.SideBySideFlag ?? false,
            pickupFlag = false,
            lotteryDropKickerList = dropList.ToArray()
        };

        context.Response.Headers["x-app-status-code"] = "0";
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-gacha-information";
        return BinaryJson(JsonSerializer.Serialize(response), key);
    }

    // /gacha/discDraw {gachaId}: one roll of the DiscGacha row's pool, with the row's draw count. The drops, the
    // duplicates and the price are economy's; the counters this payload repeats are read back after the roll.
    private async Task<IResult?> HandleGachaDiscDrawAsync(HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
    {
        const string path = "/gacha/discDraw";
        var body = await ReadBodyAsync(context.Request);
        int gachaId;
        try
        {
            using var document = JsonDocument.Parse(DecodeEitherKey(body, key));
            gachaId = IntOf(document.RootElement, "gachaId", 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode gacha disc draw request: {Error}", ex.Message);
            return StatusError(context, key);
        }

        var pool = EconomyCatalog.DiscPoolForGachaId(gachaId);
        if (pool is null || !_discGachaById.TryGetValue(gachaId, out var gacha))
        {
            _logger.LogWarning("Rejected gacha/discDraw for gacha {GachaId}: no economy pool is mapped to it", gachaId);
            return StatusError(context, key);
        }
        var (poolId, count) = pool.Value;

        EconomyRoll roll;
        try
        {
            roll = await _economy.RollAsync(await PlayerTokenAsync(ticket, context.RequestAborted), poolId, count,
                DrawIdempotencyKey(context), context.RequestAborted);
        }
        catch (OtomoIdentityException ex)
        {
            return EconomyUnavailable(context, path, ex);
        }
        catch (OtomoEconomyException ex) when (ex.Failure == OtomoEconomyFailure.Refused)
        {
            // Not enough Jet Coin, the pool is closed: the client's shape for that is a non-zero
            // x-app-status-code, and the reason is in the log.
            _logger.LogWarning("gacha/discDraw pool {Pool} x{Count} was refused by economy: {Status} {Code}",
                poolId, count, ex.StatusCode, ex.Code);
            return StatusError(context, key);
        }
        catch (OtomoEconomyException ex)
        {
            return EconomyUnavailable(context, path, ex);
        }

        // The roll has happened, so a counter read that fails falls back to the roll's own balances, never to zeroes.
        var economy = await ReadPlayerEconomyAsync(ticket, EconomyRead.Balances | EconomyRead.Inventory, path,
            context.RequestAborted);
        var snapshot = economy.Snapshot ?? SnapshotFromRoll(roll);
        var payloads = Payloads(state);

        var response = new
        {
            dropDiscList = payloads.BuildDropDiscList(roll.Draws),
            receivedUserDiscList = payloads.BuildReceivedUserDiscList(roll.Items, discId => DiscStandingOf(snapshot, discId)),
            receivedUserItemList = payloads.BuildReceivedCounters(roll.Balances, roll.Entries, roll.Items),
            userItemList = payloads.BuildUserItemList(snapshot),
            gachaDropCountRewardList = Array.Empty<object>(),
            modelId = gacha.ModelId,
            effectTopImageFilename = EffectOr(DiscGachaGroupId, gacha.EffectTopImageFilename),
            effectBottomImageFilename = EffectOr(DiscGachaGroupId, gacha.EffectBottomImageFilename),
            capsuleEffectType = CapsuleEffectOf(roll.Draws),
            drawableCount = GachaDrawableCount,
            drawableDatetime = GachaGroupStart(DiscGachaGroupId),
            completedDisplayFlag = false,
            userPresentList = Array.Empty<object>(),
            userMissionProgressList = Array.Empty<object>(),
            userDailyRandomMissionTaskList = Array.Empty<object>(),
            receivedRewardList = payloads.BuildReceivedRewards(roll.Entries, roll.Items),
            premiumFlag = false
        };

        context.Response.Headers["x-app-status-code"] = "0";
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-gacha-disc-draw";
        _logger.LogInformation("Disc gacha {GachaId} (pool {Pool}) drew {Count} for user {UserId} via economy: {Items}{Replayed}",
            gachaId, poolId, count, state.UserId, string.Join(",", (roll.Draws ?? []).Select(draw => draw.Item)),
            roll.Replayed ? " (replay)" : "");
        return BinaryJson(JsonSerializer.Serialize(response), key);
    }

    // A draw's idempotency key. A client retry of one request carries the same x-app-response-cache-id
    // (NetworkManager.SendRequestAsync), so that id makes the retry economy's replay rather than a second paid roll;
    // a request without one is a draw of its own.
    private static string DrawIdempotencyKey(HttpContext context)
    {
        var cacheId = ProgressKeyPart(context.Request.Headers["x-app-response-cache-id"].ToString());
        return cacheId.Length > 0 ? $"draw:{cacheId}" : Guid.NewGuid().ToString();
    }

    // The kicker draw: one roll on economy's cosmetic pool. The gacha id the client quotes is mapped to a pool id in
    // EconomyCatalog; the drops, the duplicate policy and the price are economy's answers, and the counters this
    // payload repeats are read back from economy after the roll.
    private async Task<IResult?> HandleGachaKickerDrawAsync(HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
    {
        var body = await ReadBodyAsync(context.Request);
        var gachaId = _kickerGachaById.Keys.OrderBy(id => id).FirstOrDefault();
        var count = 1;
        try
        {
            if (body.Length > 0)
            {
                var plaintext = DecodeEitherKey(body, key);
                using var document = JsonDocument.Parse(plaintext);
                gachaId = IntOf(document.RootElement, "gachaId", gachaId);
                count = IntOf(document.RootElement, "count", 1);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode gacha kicker draw request: {Error}", ex.Message);
            return StatusError(context, key);
        }

        var poolId = EconomyCatalog.PoolIdForGachaId(gachaId);
        if (poolId is null || count < 1 || !_kickerGachaById.TryGetValue(gachaId, out var gacha))
        {
            _logger.LogWarning("Rejected gacha/kickerDraw for gacha {GachaId} x{Count}: no economy pool is mapped to it",
                gachaId, count);
            return StatusError(context, key);
        }
        count = Math.Clamp(count, 1, MaxRollCount);

        // The received kickers show the gear in each costume's slots, which is session's: read before the roll is paid for.
        var (own, loadoutFailure) = await TryReadOwnLoadoutAsync(context, ticket, "/gacha/kickerDraw");
        if (own is null) return loadoutFailure!;

        string accessToken;
        EconomyRoll roll;
        try
        {
            accessToken = (await _identity.GetSessionForDeviceIdAsync(ticket.DeviceId, context.RequestAborted)).AccessToken;
            roll = await _economy.RollAsync(accessToken, poolId, count, DrawIdempotencyKey(context),
                context.RequestAborted);
        }
        catch (OtomoIdentityException ex)
        {
            return EconomyUnavailable(context, "/gacha/kickerDraw", ex);
        }
        catch (OtomoEconomyException ex) when (ex.Failure == OtomoEconomyFailure.Refused)
        {
            // economy refused the roll - not enough of the pool's cost, the pool is closed, nothing to draw. The
            // client's shape for that is a non-zero x-app-status-code; the reason is in the log.
            _logger.LogWarning("gacha/kickerDraw pool {Pool} x{Count} was refused by economy: {Status} {Code}",
                poolId, count, ex.StatusCode, ex.Code);
            return StatusError(context, key);
        }
        catch (OtomoEconomyException ex)
        {
            return EconomyUnavailable(context, "/gacha/kickerDraw", ex);
        }

        var economy = await ReadPlayerEconomyAsync(ticket, EconomyRead.Balances | EconomyRead.Inventory,
            "/gacha/kickerDraw", context.RequestAborted);
        var snapshot = economy.Snapshot ?? SnapshotFromRoll(roll);
        var payloads = Payloads(state, own.Loadout);

        var response = new
        {
            dropKickerCostumeList = payloads.BuildDropKickerCostumes(roll.Draws),
            receivedUserKickerList = payloads.BuildReceivedUserKickerList(roll.Draws, snapshot,
                state.KickerId, state.KickerCostumeId),
            userItemList = payloads.BuildUserItemList(snapshot),
            effectTopImageFilename = EffectOr(gacha.GachaGroupId, gacha.EffectTopImageFilename),
            effectBottomImageFilename = EffectOr(gacha.GachaGroupId, gacha.EffectBottomImageFilename),
            userPresentList = Array.Empty<object>(),
            userMissionProgressList = Array.Empty<object>(),
            userDailyRandomMissionTaskList = Array.Empty<object>(),
            receivedRewardList = Array.Empty<object>(),
            gachaBoxStatus = new { acquiredAmount = 0, totalAmount = 0 }
        };

        context.Response.Headers["x-app-status-code"] = "0";
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-gacha-kicker-draw";
        _logger.LogInformation(
            "Kicker gacha {GachaId} (pool {Pool}) drew {Count} for user {UserId} via economy: {Items}; pity {Pity}{Replayed}",
            gachaId, poolId, count, state.UserId,
            string.Join(",", (roll.Draws ?? []).Select(draw => draw.Item)), roll.Pity,
            roll.Replayed ? " (replay)" : "");
        return BinaryJson(JsonSerializer.Serialize(response), key);
    }

    // The disc tab: one ResponseGacha row per served DiscGacha row that names an economy pool, priced at that pool's
    // cost and giving that row's draw count.
    private object[] BuildHomeDiscGachaGroupList(EconomySnapshot economy)
    {
        if (!_gachaGroupById.TryGetValue(DiscGachaGroupId, out var group)) return [];

        var gachaList = new List<object>();
        foreach (var gacha in _discGachaById.Values.Where(g => g.GachaGroupId == DiscGachaGroupId).OrderBy(g => g.Id))
        {
            var mapped = EconomyCatalog.DiscPoolForGachaId(gacha.Id);
            var pool = mapped is null ? null : economy.Pools.FirstOrDefault(p => p.Id == mapped.Value.PoolId);
            if (mapped is null)
            {
                _logger.LogWarning("Disc gacha {GachaId} has no economy pool mapped to it: served as not drawable", gacha.Id);
            }
            else if (pool is null)
            {
                _logger.LogWarning("Disc gacha {GachaId} names economy pool {Pool}, which economy does not report",
                    gacha.Id, mapped.Value.PoolId);
            }

            var poolCost = PoolCost(pool);
            if (poolCost is null)
            {
                var (masterGoodsType, masterGoodsId) = CostOf(gacha.LotteryCostId);
                poolCost = (masterGoodsType, masterGoodsId, gacha.LotteryCostAmount);
            }
            var (costGoodsType, costGoodsId, costAmount) = poolCost.Value;
            // The pull button draws its cost icon from ui/goods/thumbnail/<goodsId>, and the assets are named
            // <goodsType>0001.unity3d. A raw Item row (7 for a kicker ticket) matches no asset and renders a
            // white quad, so the id sent for display is the composite. Spending is economy's pool, not this.
            costGoodsId = GoodsThumbnailId(costGoodsType, costGoodsId);
            gachaList.Add(BuildResponseGacha(gacha.Id, costGoodsType, costGoodsId, costAmount,
                pool is { Open: true } ? GachaDrawableCount : 0,
                gacha.EffectTopImageFilename, gacha.EffectBottomImageFilename, dropAmount: mapped?.Count ?? 1));
        }

        return
        [
            new
            {
                gachaGroupId = group.Id,
                name = group.Name,
                gachaType = group.GachaType,
                bannerImageFilename = group.BannerImageFilename,
                startDatetime = group.StartDatetime,
                endDatetime = group.EndDatetime,
                boxFlag = false,
                stepFlag = false,
                newFlag = false,
                completedDisplayFlag = false,
                remainingTimeDisplayFlag = false,
                gachaList = gachaList.ToArray(),
                gachaDropCountRewardList = Array.Empty<object>()
            }
        ];
    }

    // The kicker tab, one ResponseGacha row per served KickerGacha master row whose pool economy reports. The price
    // shown is the pool's own cost, so the client cannot advertise a price the purchase would not charge.
    private object[] BuildHomeKickerGachaGroupList(EconomySnapshot economy)
    {
        if (!_gachaGroupById.TryGetValue(KickerGachaGroupId, out var group)) return [];

        var gachaList = new List<object>();
        foreach (var gacha in _kickerGachaById.Values.Where(g => g.GachaGroupId == KickerGachaGroupId).OrderBy(g => g.Id))
        {
            var poolId = EconomyCatalog.PoolIdForGachaId(gacha.Id);
            var pool = poolId is null ? null : economy.Pools.FirstOrDefault(p => p.Id == poolId);
            if (poolId is null)
            {
                _logger.LogWarning("Kicker gacha {GachaId} has no economy pool mapped to it: served as not drawable", gacha.Id);
            }
            else if (pool is null)
            {
                _logger.LogWarning("Kicker gacha {GachaId} names economy pool {Pool}, which the shop does not report", gacha.Id, poolId);
            }

            var poolCost = PoolCost(pool);
            if (poolCost is null)
            {
                var (masterGoodsType, masterGoodsId) = CostOf(gacha.LotteryCostId);
                poolCost = (masterGoodsType, masterGoodsId, gacha.LotteryCostAmount);
            }
            var (costGoodsType, costGoodsId, costAmount) = poolCost.Value;
            // The pull button draws its cost icon from ui/goods/thumbnail/<goodsId>, and the assets are named
            // <goodsType>0001.unity3d. A raw Item row (7 for a kicker ticket) matches no asset and renders a
            // white quad, so the id sent for display is the composite. Spending is economy's pool, not this.
            costGoodsId = GoodsThumbnailId(costGoodsType, costGoodsId);
            gachaList.Add(BuildResponseGacha(gacha.Id, costGoodsType, costGoodsId, costAmount,
                pool is { Open: true } ? GachaDrawableCount : 0,
                gacha.EffectTopImageFilename, gacha.EffectBottomImageFilename));
        }

        return
        [
            new
            {
                gachaGroupId = group.Id,
                name = group.Name,
                gachaType = group.GachaType,
                bannerImageFilename = group.BannerImageFilename,
                startDatetime = group.StartDatetime,
                endDatetime = group.EndDatetime,
                boxFlag = false,
                stepFlag = false,
                newFlag = false,
                completedDisplayFlag = false,
                remainingTimeDisplayFlag = false,
                gachaList = gachaList.ToArray(),
                gachaDropCountRewardList = Array.Empty<object>()
            }
        ];
    }

    // The pool's price as the client's three fields: goodsType/GoodsId name the Item master row, costAmount is how
    // many of it one draw costs.
    private static (int GoodsType, int GoodsId, int Amount)? PoolCost(EconomyPool? pool)
    {
        var cost = pool?.Cost?.FirstOrDefault(grant => grant.Currency is not null && grant.Amount > 0);
        if (cost is null) return null;
        if (!EconomyCatalog.TryItemIdForCurrency(cost.Currency, out var itemId)) return null;
        if (!EconomyCatalog.TryGoodsTypeForItemId(itemId, out var goodsType)) return null;
        return (goodsType, itemId, (int)Math.Clamp(cost.Amount, 0, int.MaxValue));
    }

    // The LotteryCost master row's goodsType/goodsId, for a gacha economy has no pool for: display only.
    private (int GoodsType, int GoodsId) CostOf(int lotteryCostId) =>
        _lotteryCostById.TryGetValue(lotteryCostId, out var cost) ? (cost.GoodsType, cost.GoodsId) : (0, 0);

    // "<goodsType>0001": the variant the asset list carries for every goods type.
    private static int GoodsThumbnailId(int goodsType, int fallbackId) =>
        goodsType > 0 ? goodsType * 10000 + 1 : fallbackId;

    private static object BuildResponseGacha(int gachaId, int costGoodsType, int costGoodsId, int costAmount,
        int drawableCount, string effectTop, string effectBottom, int dropAmount = 1) => new
    {
        id = gachaId,
        dropAmount,
        drawableCount,
        // When this gacha *becomes* drawable, not when it closes. ShopKickerCellView.CheckDrawable (0x140FC14)
        // enables the Scout button only when GetRemainTime(drawableDatetime) <= 0 && drawableCount > 0, so a
        // future value here disables every row until 2030 however affordable the draw is. The window's close is
        // the group's own endDatetime.
        drawableDatetime = GachaStartDatetime,
        costGoodsType,
        costGoodsId,
        costAmount,
        gachaBoxStatus = new { acquiredAmount = 0, totalAmount = 0 },
        stepNumber = 0,
        effectTopImageFilename = effectTop,
        effectBottomImageFilename = effectBottom
    };

    // A gacha row's effect art is optional in the master and empty in ours. The client draws a white quad for
    // an empty filename, so fall back to the group's banner: something real, rather than a blank frame.
    private string EffectOr(int gachaGroupId, string effect) =>
        !string.IsNullOrEmpty(effect)
            ? effect
            : _gachaGroupById.TryGetValue(gachaGroupId, out var group) ? group.BannerImageFilename : "";

    private string GachaGroupText(int gachaGroupId, bool information) =>
        _gachaGroupById.TryGetValue(gachaGroupId, out var group)
            ? information ? group.InformationText : group.NoticeText
            : "";

    private string GachaGroupEnd(int gachaGroupId) =>
        _gachaGroupById.TryGetValue(gachaGroupId, out var group) && !string.IsNullOrEmpty(group.EndDatetime)
            ? group.EndDatetime
            : GachaEndDatetime;

    // The date a gacha became drawable. ShopKickerCellView.CheckDrawable (0x140FC14) treats drawableDatetime as
    // "not yet drawable while this is in the future", so the start of the window is the only correct value here for
    // either tab; the close is the group's EndDatetime.
    private string GachaGroupStart(int gachaGroupId) =>
        _gachaGroupById.TryGetValue(gachaGroupId, out var group) && !string.IsNullOrEmpty(group.StartDatetime)
            ? group.StartDatetime
            : GachaStartDatetime;

    private static int IntOf(JsonElement element, string name, int fallback = 0) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32()
            : fallback;

    private static double DoubleOf(JsonElement element, string name, double fallback = 0.0) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetDouble()
            : fallback;

    private static bool BoolOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private static string StrOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? ""
            : "";
}
