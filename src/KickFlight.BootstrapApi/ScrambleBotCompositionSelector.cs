namespace KickFlight.BootstrapApi;

internal sealed record ScrambleHuman(int TeamType, int KickerId);

internal sealed record ScrambleBotCandidate(int KickerId, int RoleType, string Archetype);

internal sealed record ScrambleBotDeck(
    int KickerId, int DeckId, string Profile, int Variant, int RoleType,
    IReadOnlyDictionary<string, double> CastTimes, int[] DiscIds);

internal sealed record ScrambleBotAssignment(
    int TeamType, int KickerId, int DeckId, int RoleType, string Profile, int Variant, string Archetype, int[] DiscIds);

internal sealed record ScrambleHumanParty(string Key, IReadOnlyList<ScrambleHuman> Humans);
internal sealed record ScrambleHumanTeamSelection(IReadOnlyDictionary<string, int> PartyTeams, double Score);

internal enum BattleCompositionMode
{
    CrystalScramble,
    FlagFlight,
    RapidBall
}

/// <summary>Pure mode-aware selector. Composition is optimized before selecting one registered deck per bot.</summary>
internal static class ScrambleBotCompositionSelector
{
    private const int TeamSize = 4;
    private const int DefaultDeckId = 1;
    private static readonly int[] DefaultDiscIds = [3010001, 3010002, 3010003, 3010004];

    internal static IReadOnlyList<ScrambleBotAssignment> Select(
        IReadOnlyList<ScrambleHuman> humans,
        IReadOnlyList<ScrambleBotCandidate> candidates,
        IReadOnlyList<ScrambleBotDeck> decks,
        int blueBotSlots,
        int redBotSlots,
        int seed,
        BattleCompositionMode mode = BattleCompositionMode.CrystalScramble)
    {
        if (blueBotSlots < 0 || blueBotSlots > TeamSize || redBotSlots < 0 || redBotSlots > TeamSize)
            throw new ArgumentOutOfRangeException(nameof(blueBotSlots), "Bot slot counts must fit 4v4 teams.");
        if (candidates.Count == 0 || blueBotSlots + redBotSlots == 0) return [];

        var blueHumans = humans.Where(h => h.TeamType == 0).ToArray();
        var redHumans = humans.Where(h => h.TeamType == 1).ToArray();
        var uniqueCandidates = candidates.GroupBy(c => c.KickerId).Select(g => g.First()).ToArray();
        var blue = BuildTeamOptions(blueHumans, redHumans, uniqueCandidates, blueBotSlots, mode);
        var red = BuildTeamOptions(redHumans, blueHumans, uniqueCandidates, redBotSlots, mode);
        if (blue.Count == 0 || red.Count == 0) return [];

        var pairs = new List<CompositionPair>(blue.Count * red.Count);
        foreach (var blueOption in blue)
        foreach (var redOption in red)
        {
            var repeatedAcrossSides = blueOption.Candidates.Select(c => c.KickerId)
                .Intersect(redOption.Candidates.Select(c => c.KickerId)).Count();
            var repeatedAgainstHumans = blueOption.Candidates.Count(c => redHumans.Any(h => h.KickerId == c.KickerId))
                + redOption.Candidates.Count(c => blueHumans.Any(h => h.KickerId == c.KickerId));
            // Mirrors are allowed when roles are scarce; they lose close decisions only.
            var score = blueOption.Score + redOption.Score - repeatedAcrossSides * 0.45 - repeatedAgainstHumans * 0.2;
            pairs.Add(new CompositionPair(score, blueOption.Candidates, redOption.Candidates));
        }

        if (pairs.Count == 0) return [];
        var bestScore = pairs.Max(p => p.Score);
        var nearBest = pairs.Where(p => p.Score >= bestScore - 1.25)
            .OrderBy(p => CandidateKey(p.Blue))
            .ThenBy(p => CandidateKey(p.Red))
            .ToArray();
        var chosen = nearBest[new Random(seed).Next(nearBest.Length)];

        // One deck per selected kicker; never expand deck variants during the team search.
        var assignments = new List<ScrambleBotAssignment>(blueBotSlots + redBotSlots);
        assignments.AddRange(ChooseDecks(0, blueHumans, redHumans, chosen.Blue, decks, seed, mode));
        assignments.AddRange(ChooseDecks(1, redHumans, blueHumans, chosen.Red, decks, seed ^ unchecked((int)0x5f3759df), mode));
        return assignments;
    }

    /// <summary>Assigns complete human parties to balanced teams, scoring the available bot complement jointly.</summary>
    internal static ScrambleHumanTeamSelection SelectHumanTeams(
        IReadOnlyList<ScrambleHumanParty> parties,
        IReadOnlyList<ScrambleBotCandidate> candidates,
        int seed,
        BattleCompositionMode mode = BattleCompositionMode.CrystalScramble)
    {
        var ordered = parties.OrderBy(party => party.Key, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0) return new ScrambleHumanTeamSelection(new Dictionary<string, int>(), 0);
        if (ordered.Length > 8) throw new ArgumentOutOfRangeException(nameof(parties), "A Scramble room has at most eight human parties.");

        var bestDifference = int.MaxValue;
        var bestScore = double.NegativeInfinity;
        var nearBest = new List<(int Mask, double Score, string Key)>();
        var optionCount = 1 << ordered.Length;
        for (var mask = 0; mask < optionCount; mask++)
        {
            var blue = new List<ScrambleHuman>(4);
            var red = new List<ScrambleHuman>(4);
            var blueKeys = new List<string>();
            var redKeys = new List<string>();
            for (var index = 0; index < ordered.Length; index++)
            {
                var party = ordered[index];
                var team = (mask & (1 << index)) == 0 ? 0 : 1;
                var destination = team == 0 ? blue : red;
                if (destination.Count + party.Humans.Count > TeamSize) break;
                destination.AddRange(party.Humans);
                (team == 0 ? blueKeys : redKeys).Add(party.Key);
            }
            if (blue.Count + red.Count != ordered.Sum(party => party.Humans.Count)) continue;

            // Team color is interchangeable. Keep one canonical orientation for deterministic ties.
            var blueKey = string.Join('|', blueKeys);
            var redKey = string.Join('|', redKeys);
            if (string.CompareOrdinal(blueKey, redKey) > 0) continue;

            var difference = Math.Abs(blue.Count - red.Count);
            if (difference > bestDifference) continue;
            var score = ScoreHumanPartition(blue, red, candidates, seed, mode);
            var key = $"{blueKey}||{redKey}";
            if (difference < bestDifference || score > bestScore + 1e-9)
            {
                bestDifference = difference;
                bestScore = score;
                nearBest.Clear();
                nearBest.Add((mask, score, key));
            }
            else if (Math.Abs(score - bestScore) <= 1e-9)
            {
                nearBest.Add((mask, score, key));
            }
        }

        if (nearBest.Count == 0) return new ScrambleHumanTeamSelection(new Dictionary<string, int>(), 0);
        var tied = nearBest.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
        var selected = tied[new Random(seed).Next(tied.Length)];
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < ordered.Length; index++)
            result[ordered[index].Key] = (selected.Mask & (1 << index)) == 0 ? 0 : 1;
        return new ScrambleHumanTeamSelection(result, selected.Score);
    }

    private static double ScoreHumanPartition(
        IReadOnlyList<ScrambleHuman> blue,
        IReadOnlyList<ScrambleHuman> red,
        IReadOnlyList<ScrambleBotCandidate> candidates,
        int seed,
        BattleCompositionMode mode)
    {
        var humans = blue.Concat(red).Select((human, index) =>
            new ScrambleHuman(index < blue.Count ? 0 : 1, human.KickerId)).ToArray();
        var assignments = Select(humans, candidates, [], TeamSize - blue.Count, TeamSize - red.Count, seed, mode);
        if (assignments.Count != TeamSize * 2 - blue.Count - red.Count) return double.NegativeInfinity;
        var blueBots = assignments.Where(assignment => assignment.TeamType == 0)
            .Select(assignment => new ScrambleBotCandidate(assignment.KickerId, assignment.RoleType, assignment.Archetype)).ToArray();
        var redBots = assignments.Where(assignment => assignment.TeamType == 1)
            .Select(assignment => new ScrambleBotCandidate(assignment.KickerId, assignment.RoleType, assignment.Archetype)).ToArray();
        var score = TeamScore(blue, blueBots, red, mode) + TeamScore(red, redBots, blue, mode);
        var repeatedAcrossSides = blueBots.Select(bot => bot.KickerId).Intersect(redBots.Select(bot => bot.KickerId)).Count();
        var repeatedAgainstHumans = blueBots.Count(bot => red.Any(human => human.KickerId == bot.KickerId))
            + redBots.Count(bot => blue.Any(human => human.KickerId == bot.KickerId));
        return score - repeatedAcrossSides * 0.45 - repeatedAgainstHumans * 0.2;
    }

    internal static int SeedFor(string battleId)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var value in battleId)
            {
                hash ^= value;
                hash *= 16777619;
            }
            return (int)hash;
        }
    }

    internal static BattleCompositionMode? ModeForBattleRuleId(int battleRuleId) => battleRuleId switch
    {
        1 or 5 or 6 or 7 => BattleCompositionMode.CrystalScramble,
        2 or 8 => BattleCompositionMode.FlagFlight,
        3 or 4 => BattleCompositionMode.RapidBall,
        _ => null
    };

    internal static int RoleForKicker(int kickerId) => kickerId switch
    {
        1 or 8 or 9 => 0,               // Speed
        2 or 5 or 7 or 14 => 1,         // Support
        4 or 6 or 10 or 13 => 2,        // Attack
        3 or 11 or 12 => 3,             // Tank
        _ => -1
    };

    private static List<TeamOption> BuildTeamOptions(
        IReadOnlyList<ScrambleHuman> humans,
        IReadOnlyList<ScrambleHuman> opponents,
        IReadOnlyList<ScrambleBotCandidate> candidates,
        int botSlots,
        BattleCompositionMode mode)
    {
        if (botSlots == 0) return [new TeamOption(TeamScore(humans, [], opponents, mode), [])];
        var ownHumanIds = humans.Select(h => h.KickerId).ToHashSet();
        var eligible = candidates.Where(c => !ownHumanIds.Contains(c.KickerId)).ToArray();
        // If bad/unknown human data exhausts candidates, allow same-kicker bots as a last resort.
        if (eligible.Length < botSlots) eligible = candidates.ToArray();
        if (eligible.Length < botSlots) return [];

        var options = new List<TeamOption>();
        var chosen = new ScrambleBotCandidate[botSlots];
        BuildCandidateSets(0, 0);
        return options.OrderByDescending(o => o.Score).Take(96).ToList();

        void BuildCandidateSets(int start, int count)
        {
            if (count == botSlots)
            {
                var selected = chosen.ToArray();
                options.Add(new TeamOption(TeamScore(humans, selected, opponents, mode), selected));
                return;
            }
            var needed = botSlots - count;
            for (var i = start; i <= eligible.Length - needed; i++)
            {
                chosen[count] = eligible[i];
                BuildCandidateSets(i + 1, count + 1);
            }
        }
    }

    private static IReadOnlyList<ScrambleBotAssignment> ChooseDecks(
        int teamType,
        IReadOnlyList<ScrambleHuman> humans,
        IReadOnlyList<ScrambleHuman> opponents,
        IReadOnlyList<ScrambleBotCandidate> candidates,
        IReadOnlyList<ScrambleBotDeck> decks,
        int seed,
        BattleCompositionMode mode)
    {
        var team = new List<(int KickerId, int RoleType, string Archetype)>();
        team.AddRange(humans.Select(h => (h.KickerId, RoleForKicker(h.KickerId), ArchetypeFor(h.KickerId))));
        team.AddRange(candidates.Select(c => (c.KickerId, c.RoleType, c.Archetype)));
        var assignments = new List<ScrambleBotAssignment>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var choices = decks.Where(deck => deck.KickerId == candidate.KickerId
                    && deck.RoleType == candidate.RoleType
                    && deck.DeckId > 0
                    && deck.DiscIds.Length == 4
                    && deck.DiscIds.All(id => id > 0))
                .Select(deck => (Deck: deck, Score: ProfileScore(candidate, deck, team, opponents, mode)))
                .ToArray();
            if (choices.Length == 0)
            {
                assignments.Add(new ScrambleBotAssignment(teamType, candidate.KickerId, DefaultDeckId,
                    candidate.RoleType, "legacy", 0, candidate.Archetype, DefaultDiscIds.ToArray()));
                continue;
            }

            var best = choices.Max(choice => choice.Score);
            var nearBest = choices.Where(choice => choice.Score >= best - 0.2)
                .OrderBy(choice => choice.Deck.Profile, StringComparer.Ordinal)
                .ThenBy(choice => choice.Deck.Variant)
                .ToArray();
            var deckSeed = unchecked(seed + candidate.KickerId * 7919 + index * 104729);
            var selected = nearBest[new Random(deckSeed).Next(nearBest.Length)].Deck;
            assignments.Add(new ScrambleBotAssignment(teamType, candidate.KickerId, selected.DeckId,
                selected.RoleType, selected.Profile, selected.Variant, candidate.Archetype, selected.DiscIds.ToArray()));
        }
        return assignments;
    }

    private static double TeamScore(
        IReadOnlyList<ScrambleHuman> humans,
        IReadOnlyList<ScrambleBotCandidate> bots,
        IReadOnlyList<ScrambleHuman> opponents,
        BattleCompositionMode mode)
    {
        var roles = new List<(int KickerId, int RoleType, string Archetype)>();
        roles.AddRange(humans.Select(h => (h.KickerId, RoleForKicker(h.KickerId), ArchetypeFor(h.KickerId))));
        roles.AddRange(bots.Select(b => (b.KickerId, b.RoleType, b.Archetype)));

        var score = 0.0;
        foreach (var role in new[] { 0, 1, 2, 3 })
        {
            var count = roles.Count(p => p.RoleType == role);
            var first = role switch { 0 => 2.8, 1 => 1.65, 2 => 1.25, _ => 1.7 };
            score += count == 0 ? 0 : first + Math.Max(0, count - 1) * 0.35;
            if (role == 0 && count > 2) score -= (count - 2) * 1.0;
            if (role == 1 && count > 2) score -= (count - 2) * 0.8;
        }

        var anchors = roles.Count(p => p.Archetype == "anchor");
        var carriers = roles.Count(p => p.Archetype == "carrier");
        var attackers = roles.Count(p => p.RoleType == 2 || p.Archetype is "hunter" or "mobile_pressure");
        var supports = roles.Count(p => p.RoleType == 1);
        var hasTank = roles.Any(p => p.RoleType == 3);
        if (carriers > 0) score += 1.7;
        if (carriers > 1) score -= (carriers - 1) * 1.1;
        if (anchors == 1) score += 1.3;
        if (anchors > 1) score -= (anchors - 1) * 1.5;
        if (supports >= 2 && (hasTank || carriers > 0 || attackers > 0)) score += 1.1;
        if (attackers > 0 && supports > 0) score += 0.8;

        // Ruriha sustains durable teammates; Sid pressures enemy anchors; Grenhawk supports a carrier.
        if (roles.Any(p => p.KickerId == 2) && hasTank) score += 2.1;
        if (roles.Any(p => p.KickerId == 7) && carriers > 0) score += 1.8;
        var enemyAnchors = opponents.Count(p => RoleForKicker(p.KickerId) == 3 || ArchetypeFor(p.KickerId) == "anchor");
        if (roles.Any(p => p.KickerId == 14) && enemyAnchors > 0) score += 1.9;

        // Owlbert is a defensive support substitute, not an additional tank.
        if (roles.Any(p => p.KickerId == 5) && !hasTank) score += 1.5;
        if (roles.Any(p => p.KickerId == 5) && anchors > 0) score -= 0.7;

        score += mode switch
        {
            BattleCompositionMode.FlagFlight => FlagFlightTeamScore(roles, opponents),
            BattleCompositionMode.RapidBall => RapidBallTeamScore(roles, opponents),
            _ => 0.0
        };
        return score;
    }

    private static double FlagFlightTeamScore(
        IReadOnlyList<(int KickerId, int RoleType, string Archetype)> roles,
        IReadOnlyList<ScrambleHuman> opponents)
    {
        // Keep one mobile flag carrier, a defender near the objective, and pressure for enemy carriers.
        var carriers = roles.Count(role => role.Archetype == "carrier");
        var defenders = roles.Count(role => role.Archetype == "anchor" || role.RoleType == 3);
        var hunters = roles.Count(role => role.Archetype is "hunter" or "mobile_pressure" || role.RoleType == 2);
        var enemyCarriers = opponents.Count(opponent => ArchetypeFor(opponent.KickerId) == "carrier");
        var score = carriers == 1 ? 2.8 : carriers > 1 ? 1.0 - (carriers - 1) * 1.4 : -0.7;
        score += defenders == 1 ? 1.6 : defenders > 1 ? 0.4 - (defenders - 1) * 0.7 : -0.4;
        score += hunters > 0 ? 0.9 + (enemyCarriers > 0 ? 0.8 : 0) : 0;
        return score;
    }

    private static double RapidBallTeamScore(
        IReadOnlyList<(int KickerId, int RoleType, string Archetype)> roles,
        IReadOnlyList<ScrambleHuman> opponents)
    {
        // Carriers collect and score after the enemy guardian falls; hunters pressure it while anchors protect ours.
        var carriers = roles.Count(role => role.Archetype == "carrier");
        var defenders = roles.Count(role => role.Archetype == "anchor" || role.RoleType == 3);
        var guardianPressure = roles.Count(role => role.Archetype == "hunter" || role.RoleType == 2);
        var mobileSupport = roles.Count(role => role.Archetype == "mobile_pressure");
        var enemyGuardian = opponents.Any(opponent => RoleForKicker(opponent.KickerId) == 3
            || ArchetypeFor(opponent.KickerId) == "anchor");
        var enemyCarrier = opponents.Any(opponent => ArchetypeFor(opponent.KickerId) == "carrier");
        var score = carriers > 0 ? 1.2 : -0.5;
        score += defenders == 1 ? 1.8 : defenders > 1 ? 0.5 - (defenders - 1) * 0.8 : -0.5;
        score += guardianPressure > 0 ? 1.4 + (enemyGuardian ? 1.1 : 0) + (enemyCarrier ? 0.7 : 0) : 0;
        score += mobileSupport > 0 ? 0.35 + (enemyGuardian || enemyCarrier ? 0.35 : 0) : 0;
        return score;
    }

    private static double ProfileScore(
        ScrambleBotCandidate bot,
        ScrambleBotDeck deck,
        IReadOnlyList<(int KickerId, int RoleType, string Archetype)> team,
        IReadOnlyList<ScrambleHuman> opponents,
        BattleCompositionMode mode)
    {
        var hasTank = team.Any(p => p.RoleType == 3);
        var hasCarrier = team.Any(p => p.Archetype == "carrier");
        var hasSupport = team.Any(p => p.RoleType == 1);
        var enemyTank = opponents.Any(p => RoleForKicker(p.KickerId) == 3);
        var enemyCarrier = opponents.Any(p => ArchetypeFor(p.KickerId) == "carrier");
        var baseScore = deck.Profile switch
        {
            "carrier" => bot.Archetype == "carrier" ? 3.2 : bot.RoleType == 0 ? 1.1 : -0.4,
            "hunter" => bot.Archetype is "hunter" or "mobile_pressure" ? 2.7 : enemyTank ? 1.2 : 0.2,
            "objective_attacker" => bot.Archetype is "carrier" or "mobile_pressure" ? 2.6 : 0.4,
            "healer" => bot.KickerId == 2 || bot.Archetype == "support" ? 2.0 : hasTank ? 1.2 : 0.1,
            "speed_support" => bot.RoleType == 1 && hasCarrier ? 2.0 : 0.2,
            "trap_defender" => bot.Archetype == "anchor" || bot.KickerId == 5 ? 2.0 : hasTank ? 0.8 : 0.0,
            "damage_support" => bot.RoleType == 1 && (hasTank || hasCarrier) ? 1.8 : hasSupport ? 0.4 : 0.0,
            "tank_defender" => bot.Archetype == "anchor" ? 2.8 : bot.RoleType == 3 ? 1.7 : -0.5,
            _ => 0.0
        };

        baseScore += mode switch
        {
            BattleCompositionMode.FlagFlight => deck.Profile switch
            {
                "carrier" when bot.Archetype == "carrier" => 1.6,
                "hunter" when enemyCarrier || enemyTank => 1.3,
                "trap_defender" or "tank_defender" when hasCarrier => 1.2,
                "objective_attacker" => -0.8,
                _ => 0.0
            },
            BattleCompositionMode.RapidBall => deck.Profile switch
            {
                "carrier" when bot.Archetype == "carrier" => 0.8,
                "hunter" when bot.RoleType != 1 && bot.Archetype != "mobile_pressure" && (enemyTank || enemyCarrier) => 2.0,
                "hunter" when bot.KickerId == 14 && (enemyTank || enemyCarrier) => 0.4,
                "hunter" when bot.Archetype == "mobile_pressure" && (enemyTank || enemyCarrier) => 0.2,
                "objective_attacker" when bot.Archetype == "hunter" => enemyCarrier || enemyTank ? 1.6 : 1.3,
                "objective_attacker" when bot.Archetype == "mobile_pressure" => enemyCarrier || enemyTank ? 0.4 : 0.2,
                "trap_defender" or "tank_defender" when hasTank => 1.5,
                _ => 0.0
            },
            _ => 0.0
        };

        // Preserve support identity when a carrier or tank can use ally-facing discs.
        var preservesSupportIdentity = bot.KickerId switch
        {
            7 => deck.Profile == "speed_support",
            14 => deck.Profile == "damage_support",
            _ => false
        };
        if ((mode is BattleCompositionMode.FlagFlight or BattleCompositionMode.RapidBall)
            && preservesSupportIdentity && (hasCarrier || hasTank))
            baseScore += mode == BattleCompositionMode.FlagFlight ? 2.3 : 1.7;

        // Catalog variants are prefiltered by cast-timing fit. Keep the per-kicker master cast profile as a
        // small tie-breaker: faster activation matters most for carrier, movement and hunt profiles.
        var categories = deck.Profile switch
        {
            "carrier" or "objective_attacker" => new[] { "ATK(Rush)", "MOVE", "WARP" },
            "hunter" => new[] { "ATK(Short Distance)", "ATK(Long Distance)", "ATK(Circumference)", "ATK(Rush)" },
            "healer" => new[] { "HEAL" },
            "speed_support" => new[] { "MOVE", "BUFF" },
            "trap_defender" or "tank_defender" => new[] { "TRAP", "HEAL" },
            "damage_support" => new[] { "BUFF", "ATK(Rush)" },
            _ => Array.Empty<string>()
        };
        var applicable = categories.Where(deck.CastTimes.ContainsKey).Select(category => deck.CastTimes[category]).ToArray();
        if (applicable.Length == 0) return baseScore;
        return baseScore + Math.Clamp((1.5 - applicable.Average()) * 0.45, -0.45, 0.45);
    }

    private static string CandidateKey(IReadOnlyList<ScrambleBotCandidate> candidates) =>
        string.Join(',', candidates.Select(candidate => candidate.KickerId.ToString("D2")));

    private static string ArchetypeFor(int kickerId) => kickerId switch
    {
        1 or 8 or 9 => "carrier",
        2 => "support",
        3 or 11 or 12 => "anchor",
        5 => "owlbert_support",
        7 => "mobile_pressure",
        10 or 14 => "hunter",
        _ => "damage"
    };

    private sealed record TeamOption(double Score, ScrambleBotCandidate[] Candidates);
    private sealed record CompositionPair(double Score, ScrambleBotCandidate[] Blue, ScrambleBotCandidate[] Red);
}
