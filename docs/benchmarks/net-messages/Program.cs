using System;
using System.Collections.Generic;
using System.Diagnostics;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Network.Enum;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;

static partial class Program
{
    static void Main(string[] args)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var mode = args.Length > 0 ? args[0] : "sizes";
        switch (mode)
        {
            case "sizes": Sizes(); break;
            case "parts": Parts.Run(); break;
            case "proto": Proto.Run(); break;
            case "inbound": Inbound.Run(); break;
            case "caps": CapScan.Run(); break;
            case "fanout": Fanout.Run(); break;
            case "ench": Ench.Run(); break;
            default: Console.WriteLine("modes: sizes | parts | proto | inbound"); break;
        }
    }

    public static (double bytes, double ns) Measure(Action a, int iters = 20000)
    {
        for (int i = 0; i < 2000; i++) a();            // warm up / tier up
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++) a();
        sw.Stop();
        long after = GC.GetAllocatedBytesForCurrentThread();
        return ((after - before) / (double)iters, sw.Elapsed.TotalMilliseconds * 1e6 / iters);
    }

    static void Row(string name, Func<GameMessage> make)
    {
        var m = make();
        var len = m.Data.Length;
        var cap = m.Data.Capacity;
        var (bytes, ns) = Measure(() => make());
        Console.WriteLine($"{name,-44} len={len,5} cap={cap,5} {(len > cap ? "" : "")}alloc/msg={bytes,7:F0} B  {ns,7:F0} ns");
    }

    static void Sizes()
    {
        var wo = Stubs.MakeWorldObject();
        var other = Stubs.MakeWorldObject(0x80005678);
        var s = Stubs.MakeSession();
        string name14 = "Drudge Skulker";
        string chat40 = "Anyone selling a Pyreal Mote? PST please";   // 40 chars

        Console.WriteLine("=== GameMessage (len = bytes written incl. 4-byte opcode; cap = final MemoryStream capacity) ===");
        Row("UpdatePosition (no velocity, no placement)", () => new GameMessageUpdatePosition(wo));
        Row("UpdateMotion (Ready, NonCombat)", () => new GameMessageUpdateMotion(wo, new Motion(MotionStance.NonCombat, MotionCommand.Ready)));
        Row("UpdateMotion (attack anim, speed 1.25)", () => new GameMessageUpdateMotion(wo, new Motion(MotionStance.SwordCombat, MotionCommand.AttackHigh1, 1.25f)));
        Row("VectorUpdate", () => new GameMessageVectorUpdate(wo));
        Row("Sound", () => new GameMessageSound(wo.Guid, Sound.HitFlesh1, 0.5f));
        Row("Script", () => new GameMessageScript(wo.Guid, PlayScript.Destroy, 1f));
        Row("PublicUpdatePropertyInt", () => new GameMessagePublicUpdatePropertyInt(wo, PropertyInt.CombatMode, 2));
        Row("PrivateUpdatePropertyInt", () => new GameMessagePrivateUpdatePropertyInt(wo, PropertyInt.EncumbranceVal, 2000));
        Row("PrivateUpdatePropertyInt64", () => new GameMessagePrivateUpdatePropertyInt64(wo, PropertyInt64.AvailableExperience, 2000));
        Row("PublicUpdatePropertyFloat", () => new GameMessagePublicUpdatePropertyFloat(wo, PropertyFloat.HealthRate, 1.0));
        Row("PrivateUpdateAttribute2ndLevel", () => new GameMessagePrivateUpdateAttribute2ndLevel(wo, Vital.Health, 120));
        Row("SetState", () => new GameMessageSetState(wo, ACE.Entity.Enum.PhysicsState.Ethereal));
        Row("PickupEvent", () => new GameMessagePickupEvent(wo));
        Row("DeleteObject", () => new GameMessageDeleteObject(wo));
        Row("SetStackSize", () => new GameMessageSetStackSize(wo));
        Row("PublicUpdateInstanceID", () => new GameMessagePublicUpdateInstanceID(wo, PropertyInstanceId.Wielder, other.Guid));
        Row("PrivateUpdatePropertyString (14 ch)", () => new GameMessagePrivateUpdatePropertyString(wo, PropertyString.Name, name14));
        Row("HearSpeech (40 ch msg, 14 ch name)", () => new GameMessageHearSpeech(chat40, name14, wo.Guid.Full, ChatMessageType.Speech));
        Row("HearRangedSpeech (40/14)", () => new GameMessageHearRangedSpeech(chat40, name14, wo.Guid.Full, 10f, ChatMessageType.Speech));
        Row("EmoteText (14/40)", () => new GameMessageEmoteText(wo.Guid.Full, name14, chat40));
        Row("SystemChat (40 ch)", () => new GameMessageSystemChat(chat40, ChatMessageType.Broadcast));
        Row("SystemChat (120 ch)", () => new GameMessageSystemChat(chat40 + chat40 + chat40, ChatMessageType.Broadcast));
        try { Row("CreateObject (bare stub WO, no ObjDesc, no PhysicsObj)", () => new GameMessageCreateObject(wo)); } catch (Exception e) { Console.WriteLine("CreateObject failed: " + e.GetType().Name + ": " + e.Message.Split('\n')[0]); }
        Row("SoulEmote (14/40)", () => new GameMessageSoulEmote(wo.Guid.Full, name14, chat40));

        Console.WriteLine();
        Console.WriteLine("=== GameEvent (16-byte header: opcode, guid, seq, eventType) ===");
        Row("GameEventWeenieError", () => new GameEventWeenieError(s, WeenieError.YoureTooBusy));
        Row("GameEventUseDone", () => new GameEventUseDone(s, WeenieError.None));
        Row("GameEventAttackDone", () => new GameEventAttackDone(s));
        Row("GameEventCombatCommenceAttack", () => new GameEventCombatCommenceAttack(s));
        Row("GameEventUpdateHealth", () => new GameEventUpdateHealth(s, 0x80001234, 0.5f));
        Row("GameEventWieldItem", () => new GameEventWieldItem(s, 0x80001234, EquipMask.MeleeWeapon));
        Row("GameEventItemServerSaysContainId", () => new GameEventItemServerSaysContainId(s, wo, other));
        Row("GameEventItemServerSaysMoveItem", () => new GameEventItemServerSaysMoveItem(s, wo));
        Row("GameEventMagicUpdateSpell", () => new GameEventMagicUpdateSpell(s, 1234));
        Row("GameEventMagicRemoveEnchantment", () => new GameEventMagicRemoveEnchantment(s, 1234, 1));
        Row("GameEventAttackerNotification (14 ch)", () => new GameEventAttackerNotification(s, name14, DamageType.Slash, 0.1f, 25, false, AttackConditions.None));
        Row("GameEventDefenderNotification (14 ch)", () => new GameEventDefenderNotification(s, name14, DamageType.Slash, 0.1f, 25, DamageLocation.Chest, false, AttackConditions.None));
        Row("GameEventEvasionAttackerNotification", () => new GameEventEvasionAttackerNotification(s, name14));
        Row("GameEventEvasionDefenderNotification", () => new GameEventEvasionDefenderNotification(s, name14));
        Row("GameEventCommunicationTransientString 40", () => new GameEventCommunicationTransientString(s, chat40));
        Row("GameEventTell (40/14)", () => new GameEventTell(s, chat40, name14, 1, 2, ChatMessageType.Tell));
        Row("GameEventChannelBroadcast (14/40)", () => new GameEventChannelBroadcast(s, ACE.Entity.Enum.Channel.Fellow, name14, chat40));
        Row("GameEventPopupString (40)", () => new GameEventPopupString(s, chat40));
        var ench = (ACE.Server.Network.Structure.Enchantment)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ACE.Server.Network.Structure.Enchantment)); ench.HasSpellSetID = 1;
        Row("GameEventMagicUpdateEnchantment", () => new GameEventMagicUpdateEnchantment(s, ench));
        Row("GameEventKillerNotification (60)", () => new GameEventKillerNotification(s, chat40 + "abcdefghijklmnopqrst"));
    }
}
