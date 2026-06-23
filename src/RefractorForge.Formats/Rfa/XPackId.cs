namespace RefractorForge.Formats.Rfa;

// ── Public types ─────────────────────────────────────────────────────────────

/// <summary>The expansion-pack / mod-DLL binding stored in the RFA header.
/// The engine reads this to decide which game DLL to load.</summary>
public enum XPackId : uint
{
    Default       = 0x48128321,   // base game
    RoadToRome    = 0x52382184,   // XPack1  — Battlefield 1942: Road to Rome
    SecretWeapons = 0x71629419,   // XPack2  — Battlefield 1942: Secret Weapons of WWII
    None          = 0x81671213,   // no mod DLL
}
