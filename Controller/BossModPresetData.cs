namespace FateWalker.Controller;

/// <summary>
/// BossMod preset JSON for FateWalker. Two variants:
///   • <see cref="PresetWithJobModules"/> — full preset including every xan job
///     rotation module. Used when CombatBackend = BossMod.
///   • <see cref="PresetWithoutJobModules"/> — same AI / movement / target /
///     FATE-utility modules but no job rotation. Used when CombatBackend = RSR
///     or Manual, so BossMod handles target picking + movement + AoE avoidance
///     while RSR (or the player) handles skill rotation.
/// </summary>
public static class BossModPresetData
{
    public const string PresetName = "FateWalker - FATE";

    // Shared header for both variants — must use double braces so the raw string
    // remains a valid JSON template after the variant tail is appended.
    private const string CommonHeader = """
{
  "Name": "FateWalker - FATE",
  "Modules": {
    "BossMod.Autorotation.MiscAI.FateUtils": [
      { "Track": "Handin",  "Option": "Enabled" },
      { "Track": "Collect", "Option": "Enabled" },
      { "Track": "Sync",    "Option": "Enable"  },
      { "Track": "Chocobo", "Option": "Enabled" }
    ],
    "BossMod.Autorotation.MiscAI.AutoTarget": [
      { "Track": "General",  "Option": "Aggressive" },
      { "Track": "Retarget", "Option": "Hostiles"   },
      { "Track": "FATE",     "Option": "Enabled"    }
    ],
    "BossMod.Autorotation.MiscAI.NormalMovement": [
      { "Track": "Destination", "Option": "Pathfind" }
    ],
    "BossMod.Autorotation.MiscAI.StayCloseToTarget": [
      { "Track": "Range", "Option": "OnHitbox" }
    ],

    "BossMod.Autorotation.xan.HealerAI": [
      { "Track": "Esuna2", "Option": "HintOnly" }
    ],
    "BossMod.Autorotation.xan.MeleeAI":  [],
    "BossMod.Autorotation.xan.TankAI":   [],
    "BossMod.Autorotation.xan.RangedAI": []
""";

    /// <summary>
    /// Full preset with all xan job rotation modules. Active job's module
    /// matches and fires skills; others are inert.
    /// </summary>
    public const string PresetWithJobModules = CommonHeader + """
,

    "BossMod.Autorotation.xan.PLD": [
      { "Track": "Buffs",     "Option": "Automatic" },
      { "Track": "AOE",       "Option": "AOE"       },
      { "Track": "Targeting", "Option": "Manual"    }
    ],
    "BossMod.Autorotation.VeynWAR": [
      { "Track": "AOE", "Option": "AutoFinishCombo" }
    ],
    "BossMod.Autorotation.xan.DRK": [
      { "Track": "Buffs",     "Option": "Automatic" },
      { "Track": "AOE",       "Option": "AOE"       },
      { "Track": "Targeting", "Option": "Manual"    }
    ],
    "BossMod.Autorotation.xan.GNB": [
      { "Track": "Buffs",     "Option": "Automatic" },
      { "Track": "AOE",       "Option": "AOE"       },
      { "Track": "Targeting", "Option": "Manual"    }
    ],

    "BossMod.Autorotation.xan.WHM": [
      { "Track": "Buffs",     "Option": "Automatic" },
      { "Track": "AOE",       "Option": "AOE"       },
      { "Track": "Targeting", "Option": "Manual"    }
    ],
    "BossMod.Autorotation.xan.SCH": [
      { "Track": "Buffs",     "Option": "Automatic" },
      { "Track": "AOE",       "Option": "AOE"       },
      { "Track": "Targeting", "Option": "Manual"    }
    ],
    "BossMod.Autorotation.xan.AST": [
      { "Track": "Buffs",     "Option": "Automatic" },
      { "Track": "AOE",       "Option": "AOE"       },
      { "Track": "Targeting", "Option": "Manual"    }
    ],
    "BossMod.Autorotation.xan.SGE": [
      { "Track": "AOE",       "Option": "AOE"       },
      { "Track": "Targeting", "Option": "Manual"    }
    ],

    "BossMod.Autorotation.xan.MNK": [
      { "Track": "AOE",       "Option": "AOE"       },
      { "Track": "Targeting", "Option": "Manual"    }
    ],
    "BossMod.Autorotation.xan.DRG": [
      { "Track": "Buffs",     "Option": "Automatic" },
      { "Track": "AOE",       "Option": "AOE"       },
      { "Track": "Targeting", "Option": "Manual"    }
    ],
    "BossMod.Autorotation.xan.NIN": [
      { "Track": "Buffs",     "Option": "Automatic" },
      { "Track": "AOE",       "Option": "AOE"       },
      { "Track": "Targeting", "Option": "Manual"    }
    ],
    "BossMod.Autorotation.xan.SAM": [
      { "Track": "Buffs",     "Option": "Automatic" },
      { "Track": "AOE",       "Option": "AOE"       },
      { "Track": "Targeting", "Option": "Manual"    }
    ],
    "BossMod.Autorotation.xan.RPR": [
      { "Track": "Buffs",     "Option": "Automatic" },
      { "Track": "AOE",       "Option": "AOE"       },
      { "Track": "Targeting", "Option": "Manual"    }
    ],
    "BossMod.Autorotation.xan.VPR": [
      { "Track": "Buffs",     "Option": "Automatic" },
      { "Track": "AOE",       "Option": "AOE"       },
      { "Track": "Targeting", "Option": "Manual"    }
    ],

    "BossMod.Autorotation.xan.BRD": [
      { "Track": "Buffs",     "Option": "Automatic" },
      { "Track": "AOE",       "Option": "AOE"       },
      { "Track": "Targeting", "Option": "Manual"    }
    ],
    "BossMod.Autorotation.xan.MCH": [
      { "Track": "Buffs",     "Option": "Automatic" },
      { "Track": "AOE",       "Option": "AOE"       },
      { "Track": "Targeting", "Option": "Manual"    }
    ],
    "BossMod.Autorotation.xan.DNC": [
      { "Track": "Buffs",     "Option": "Automatic" },
      { "Track": "AOE",       "Option": "AOE"       },
      { "Track": "Targeting", "Option": "Manual"    }
    ],

    "BossMod.Autorotation.xan.BLM": [
      { "Track": "AOE",       "Option": "AOE"       },
      { "Track": "Targeting", "Option": "Manual"    }
    ],
    "BossMod.Autorotation.xan.SMN": [
      { "Track": "Buffs",     "Option": "Automatic" },
      { "Track": "AOE",       "Option": "AOE"       },
      { "Track": "Targeting", "Option": "Manual"    }
    ],
    "BossMod.Autorotation.xan.RDM": [
      { "Track": "Buffs",     "Option": "Automatic" },
      { "Track": "AOE",       "Option": "AOE"       },
      { "Track": "Targeting", "Option": "Manual"    }
    ],
    "BossMod.Autorotation.xan.PCT": [
      { "Track": "Buffs",     "Option": "Automatic" },
      { "Track": "AOE",       "Option": "AOE"       },
      { "Track": "Targeting", "Option": "Manual"    },
      { "Track": "Motifs",    "Option": "Downtime"  }
    ]
  }
}
""";

    /// <summary>
    /// Lean preset — only AI / targeting / movement / FATE utility modules.
    /// Use when an external rotation plugin (RSR) is handling skill output.
    /// </summary>
    public const string PresetWithoutJobModules = CommonHeader + """

  }
}
""";

    public static string ForBackend(Configuration.CombatBackendKind backend) =>
        backend == Configuration.CombatBackendKind.BossMod
            ? PresetWithJobModules
            : PresetWithoutJobModules;
}
