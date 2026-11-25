using HarmonyLib;

namespace ValPerfPatcher
{
    /// <summary>
    /// Base game, triggered by mod creatures. Procreation.Procreate runs every m_updateInterval seconds on every creature
    /// that has a Procreation component, and its first check is m_tameable.IsTamed(). A mod creature with Procreation
    /// but no Tameable (Bestiary's RDB_fox: m_tameable null) throws a NullReferenceException there on every update, for
    /// every such creature you own: 191 logged stack traces in a 2 h session.
    /// Only tamed creatures can breed, so a creature that cannot be tamed never gets past that check anyway. This runs
    /// last among the prefixes (Bestiary's lion prefix sets m_tameable itself first) and, when m_tameable is still empty,
    /// picks up a Tameable added after Awake if there is one, and otherwise skips the call. Same result, no exception.
    /// </summary>
    internal static class UntameableProcreationFix
    {
        private static AccessTools.FieldRef<Procreation, Tameable> _tameable;

        public static string Apply(Harmony harmony)
        {
            var procreate = AccessTools.DeclaredMethod(typeof(Procreation), "Procreate", System.Type.EmptyTypes);
            if (procreate == null || AccessTools.DeclaredField(typeof(Procreation), "m_tameable")?.FieldType != typeof(Tameable))
                return "the game's breeding code has changed since this fix was written";
            _tameable = AccessTools.FieldRefAccess<Procreation, Tameable>("m_tameable");
            harmony.Patch(procreate, prefix: new HarmonyMethod(typeof(UntameableProcreationFix), nameof(Prefix)) { priority = Priority.Last });
            return null;
        }

        private static bool Prefix(Procreation __instance, bool __runOriginal)
        {
            if (!__runOriginal) return false; // another mod's prefix already handled this call
            if (_tameable(__instance) != null) return true;
            var tameable = __instance.GetComponent<Tameable>();
            if (tameable == null) return false; // cannot be tamed, so cannot breed
            _tameable(__instance) = tameable;
            return true;
        }
    }
}
