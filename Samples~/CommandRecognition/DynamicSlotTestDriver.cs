using UnityEngine;
using VoskXR.Commands;

/// <summary>
/// Manual test driver for Phase 1–3 of the v4.0 test matrix.
/// Drop onto the CommandDemo scene alongside VoskCommandRecogniser.
/// Click a step button to set provider state, then inject text via the
/// Command Debug window (Window > VOSK XR > Command Debug).
/// </summary>
public class DynamicSlotTestDriver : MonoBehaviour
{
    [SerializeField] VoskCommandRecogniser commandRecogniser;

    // -------- Phase 1: Dynamic Slot Filtering --------

    [ContextMenu("1.1–1.2  Clear All Providers")]
    public void Step_1_1_ClearProviders()
    {
        commandRecogniser.UnregisterSlotValueProvider("target");
        commandRecogniser.UnregisterSlotValueProvider("weapon");
        commandRecogniser.NotifySlotChanged();
        Debug.Log("[Phase1] 1.1–1.2: All providers cleared. Inject baseline texts.");
    }

    [ContextMenu("1.3–1.4  Target = hotel one only")]
    public void Step_1_3_TargetHotelOneOnly()
    {
        commandRecogniser.RegisterSlotValueProvider("target",
            () => new[] { "hotel one" });
        commandRecogniser.NotifySlotChanged();
        Debug.Log("[Phase1] 1.3–1.4: Target provider = [hotel one]. " +
                  "Inject 'launch missiles target hotel one' (should match), " +
                  "then 'launch missiles target hotel two' (should reject).");
    }

    [ContextMenu("1.5  Unregister target provider")]
    public void Step_1_5_UnregisterTarget()
    {
        commandRecogniser.UnregisterSlotValueProvider("target");
        commandRecogniser.NotifySlotChanged();
        Debug.Log("[Phase1] 1.5: Target provider removed. " +
                  "Inject 'launch missiles target hotel two' (should match — full values restored).");
    }

    [ContextMenu("1.6  Target provider returns null")]
    public void Step_1_6_TargetNull()
    {
        commandRecogniser.RegisterSlotValueProvider("target", () => null);
        commandRecogniser.NotifySlotChanged();
        Debug.Log("[Phase1] 1.6: Target provider = null. " +
                  "Inject 'launch missiles target alpha one' (should match — null = full static values).");
    }

    [ContextMenu("1.7  Target provider returns empty")]
    public void Step_1_7_TargetEmpty()
    {
        commandRecogniser.RegisterSlotValueProvider("target",
            () => new string[0]);
        commandRecogniser.NotifySlotChanged();
        Debug.Log("[Phase1] 1.7: Target provider = []. " +
                  "Inject 'launch missiles target hotel one' (should reject — nothing matches).");
    }

    [ContextMenu("1.8–1.9  Target = hotel one (test aliases)")]
    public void Step_1_8_TargetHotelOneAliases()
    {
        commandRecogniser.RegisterSlotValueProvider("target",
            () => new[] { "hotel one" });
        commandRecogniser.NotifySlotChanged();
        Debug.Log("[Phase1] 1.8–1.9: Target provider = [hotel one]. " +
                  "Inject 'launch missiles target h one' (should match via alias), " +
                  "then 'launch missiles target h two' (should reject — alias pruned).");
    }

    // -------- Phase 2: Provider Lifecycle & Edge Cases --------

    [ContextMenu("2.1  Register without notify")]
    public void Step_2_1_RegisterWithoutNotify()
    {
        // Reset to full parser first (clears any leftover state from Phase 1)
        commandRecogniser.UnregisterSlotValueProvider("target");
        commandRecogniser.UnregisterSlotValueProvider("weapon");
        commandRecogniser.NotifySlotChanged();

        // Now register without notify — parser should still have full values
        commandRecogniser.RegisterSlotValueProvider("target",
            () => new[] { "hotel one" });
        // Deliberately NOT calling NotifySlotChanged
        Debug.Log("[Phase2] 2.1: Reset to full parser, then registered provider WITHOUT notify. " +
                  "Inject 'launch missiles target hotel two' (should still match old parser).");
    }

    [ContextMenu("2.2  Now notify")]
    public void Step_2_2_NowNotify()
    {
        commandRecogniser.NotifySlotChanged();
        Debug.Log("[Phase2] 2.2: NotifySlotChanged called. " +
                  "Inject 'launch missiles target hotel two' (should reject now).");
    }

    [ContextMenu("2.3–2.4  Two-slot providers")]
    public void Step_2_3_TwoSlotProviders()
    {
        commandRecogniser.RegisterSlotValueProvider("target",
            () => new[] { "hotel one" });
        commandRecogniser.RegisterSlotValueProvider("weapon",
            () => new[] { "missiles" });
        commandRecogniser.NotifySlotChanged();
        Debug.Log("[Phase2] 2.3–2.4: target=[hotel one], weapon=[missiles]. " +
                  "Inject 'launch torpedoes target hotel one' (reject — torpedoes excluded), " +
                  "then 'launch missiles target hotel one' (should match).");
    }

    [ContextMenu("2.5  Bogus slot provider")]
    public void Step_2_5_BogusSlot()
    {
        commandRecogniser.UnregisterSlotValueProvider("target");
        commandRecogniser.UnregisterSlotValueProvider("weapon");
        commandRecogniser.RegisterSlotValueProvider("bogus",
            () => new[] { "whatever" });
        commandRecogniser.NotifySlotChanged();
        Debug.Log("[Phase2] 2.5: Provider on non-existent slot 'bogus'. " +
                  "Inject 'launch missiles target hotel one' (should match normally).");
    }

    [ContextMenu("2.6  Rapid toggle")]
    public void Step_2_6_RapidToggle()
    {
        commandRecogniser.UnregisterSlotValueProvider("bogus");

        commandRecogniser.RegisterSlotValueProvider("target",
            () => new[] { "hotel one" });
        commandRecogniser.NotifySlotChanged();

        commandRecogniser.RegisterSlotValueProvider("target",
            () => new[] { "hotel two" });
        commandRecogniser.NotifySlotChanged();

        commandRecogniser.RegisterSlotValueProvider("target",
            () => new[] { "alpha one" });
        commandRecogniser.NotifySlotChanged();

        Debug.Log("[Phase2] 2.6: Rapidly toggled target provider 3 times. Final = [alpha one]. " +
                  "Inject 'launch missiles target alpha one' (should match).");
    }

    [ContextMenu("2.7  RebuildGrammar with provider")]
    public void Step_2_7_RebuildGrammarWithProvider()
    {
        commandRecogniser.RegisterSlotValueProvider("target",
            () => new[] { "hotel one" });
        commandRecogniser.NotifySlotChanged();
        commandRecogniser.RebuildGrammar();
        Debug.Log("[Phase2] 2.7: Provider target=[hotel one], then RebuildGrammar(). " +
                  "Inject 'launch missiles target hotel two' (should reject — provider survives rebuild).");
    }

    // -------- Phase 3: Integration with Command Sets --------

    [ContextMenu("3.1–3.2  All sets + target provider")]
    public void Step_3_1_AllSetsWithProvider()
    {
        commandRecogniser.SetActiveSets("weapons", "navigation", "common");
        commandRecogniser.RegisterSlotValueProvider("target",
            () => new[] { "hotel one" });
        commandRecogniser.NotifySlotChanged();
        Debug.Log("[Phase3] 3.1–3.2: All sets active, target=[hotel one]. " +
                  "Inject 'launch missiles target hotel one' (should match), " +
                  "then 'orient heading two seven zero' (should match — NumberSequence unaffected).");
    }

    [ContextMenu("3.3  Navigation only")]
    public void Step_3_3_NavigationOnly()
    {
        commandRecogniser.SetActiveSets("navigation");
        Debug.Log("[Phase3] 3.3: Only navigation set active (target provider still registered). " +
                  "Inject 'launch missiles target hotel one' (should reject — not in active set).");
    }

    [ContextMenu("3.4–3.5  Weapons only")]
    public void Step_3_4_WeaponsOnly()
    {
        commandRecogniser.SetActiveSets("weapons");
        Debug.Log("[Phase3] 3.4–3.5: Weapons set active (target provider still [hotel one]). " +
                  "Inject 'launch missiles target hotel one' (should match), " +
                  "then 'launch missiles target hotel two' (should reject).");
    }

    [ContextMenu("3.6  Unregister and restore")]
    public void Step_3_6_UnregisterRestore()
    {
        commandRecogniser.UnregisterSlotValueProvider("target");
        commandRecogniser.NotifySlotChanged();
        Debug.Log("[Phase3] 3.6: Target provider removed, sets still active. " +
                  "Inject 'launch missiles target hotel two' (should match — full values restored).");
    }

    // -------- Utility --------

    [ContextMenu("Reset All")]
    public void ResetAll()
    {
        commandRecogniser.UnregisterSlotValueProvider("target");
        commandRecogniser.UnregisterSlotValueProvider("weapon");
        commandRecogniser.UnregisterSlotValueProvider("bogus");
        commandRecogniser.NotifySlotChanged();
        commandRecogniser.SetActiveSets("weapons", "navigation", "common");
        Debug.Log("[TestDriver] All providers cleared, all sets active.");
    }
}
