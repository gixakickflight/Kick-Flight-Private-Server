"""Smoke-check that legal party allocations can be filled to 4v4.

The .NET matchmaking tests cover the actual Scramble allocator, including whole-party
placement, synergy, entry-order independence, and the preserved non-Scramble/Gym paths.
This small helper only checks the final bot-slot arithmetic for representative human
splits, including the unavoidable 3/1 split from a three-person party plus a solo.
"""

MAX_PER_TEAM = 4
TOTAL_PLAYERS = MAX_PER_TEAM * 2


def fill_bots(blue_humans, red_humans):
    if not 0 <= blue_humans <= MAX_PER_TEAM or not 0 <= red_humans <= MAX_PER_TEAM:
        raise ValueError("human team counts must fit 4v4")
    teams = [0] * blue_humans + [1] * red_humans
    teams.extend([0] * (MAX_PER_TEAM - blue_humans))
    teams.extend([1] * (MAX_PER_TEAM - red_humans))
    return teams


for blue_humans, red_humans in ((1, 0), (1, 1), (3, 1), (2, 2), (3, 2), (4, 3), (4, 4)):
    teams = fill_bots(blue_humans, red_humans)
    assert len(teams) == TOTAL_PLAYERS, f"{blue_humans}/{red_humans} humans: expected 8 players, got {len(teams)}"
    assert teams.count(0) == MAX_PER_TEAM and teams.count(1) == MAX_PER_TEAM, \
        f"{blue_humans}/{red_humans} humans: expected 4v4, got {teams}"

print("OK: representative balanced and party-constrained human splits fill to 4v4")
