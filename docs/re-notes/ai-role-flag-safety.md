# Experimental AI role cave flag safety

- Review identified DIAG probe 7800 at 0x159D420 and 6410 at 0x159D764 overlapping role caves.
- Patcher now rejects KF_AI_ROLES=1 plus KF_DIAG=1 before binary access/writes. Targeting/deposit each require roles. All flags default OFF.
- Tests execute the real patcher module under isolated environments: default, telemetry, targeting, deposit, prerequisite failures and overlapping flags.
- Effective role patch ranges are checked against every active ARM64 patch for normal roles, standalone KF_DAMAGE_DIAG+KF_PHOTON0 and KF_RESULT_DIAG: zero overlaps. These two independent diagnostic sets remain allowed.
- Python role suites: 28 tests passed with project .venv, Keystone/Capstone and optional Unicorn2.1.4 for actual cave execution. Android gameplay remains a separate validation.
