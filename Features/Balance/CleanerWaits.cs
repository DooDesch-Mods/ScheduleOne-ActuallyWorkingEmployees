using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ActuallyWorkingEmployees.Compat;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Networking;
using BagIter = Il2CppScheduleOne.NPCs.Behaviour.BagTrashCanBehaviour.ObjectCompilerGeneratedNPrivateSealedIEnumerator1ObjectIEnumeratorIDisposableInObBaObObUnique;
using DropIter = Il2CppScheduleOne.NPCs.Behaviour.DisposeTrashBagBehaviour.ObjectCompilerGeneratedNPrivateSealedIEnumerator1ObjectIEnumeratorIDisposableInObDiVoObMoInVoBoOb0;
using GrabIter = Il2CppScheduleOne.NPCs.Behaviour.DisposeTrashBagBehaviour.ObjectCompilerGeneratedNPrivateSealedIEnumerator1ObjectIEnumeratorIDisposableInObDiVoObMoInVoBoOb1;
using GrabberIter = Il2CppScheduleOne.NPCs.Behaviour.EmptyTrashGrabberBehaviour.ObjectCompilerGeneratedNPrivateSealedIEnumerator1ObjectIEnumeratorIDisposableInObEmObObUnique;
using PickUpIter = Il2CppScheduleOne.NPCs.Behaviour.PickUpTrashBehaviour.ObjectCompilerGeneratedNPrivateSealedIEnumerator1ObjectIEnumeratorIDisposableInObPiObObUnique;

namespace ActuallyWorkingEmployees.Features.Balance
{
    /// <summary>
    /// The cleaner behaviours (pick up trash, bag a bin, dispose a bag, empty the grabber) wait with literal
    /// <c>WaitForSeconds</c> values inside coroutines and read no work speed. After every step of such a coroutine the
    /// wait it just yielded is rescaled in place, on the host only: the host's pickup, bagging and disposal decide the
    /// gameplay, a client only plays the animation with the game's own timing.
    /// </summary>
    internal static class CleanerWaits
    {
        internal enum Kind { Any, Pickup, Bag, Dispose, Grabber }

        // Sum of the literal waits of one action, which the option's seconds replace.
        private const float PickupVanilla = 0.9f;
        private const float BagVanilla = 3f;
        private const float DisposeVanilla = 1.2f;

        private static readonly float[] _before = new float[5];
        private static readonly float[] _after = new float[5];

        /// <summary>Last wait the patch rescaled for one option, for the dev command. Formatted on request.</summary>
        internal static string Seen(Kind kind)
        {
            int i = (int)kind;
            if (_after[i] <= 0f) return "not seen yet";
            return "last wait " + Eff.F(_before[i]) + " s -> " + Eff.F(_after[i]) + " s";
        }

        internal static void Clear() { Array.Clear(_before, 0, _before.Length); Array.Clear(_after, 0, _after.Length); }

        /// <summary>Rescales the wait the coroutine <paramref name="iterator"/> yielded last.</summary>
        internal static void Rescale(Il2CppSystem.Object iterator)
        {
            Kind kind;
            Il2CppSystem.Object current;
            PickUpIter pick = iterator.TryCast<PickUpIter>();
            if (pick != null) { kind = Kind.Pickup; current = pick.__2__current; }
            else
            {
                BagIter bag = iterator.TryCast<BagIter>();
                if (bag != null) { kind = Kind.Bag; current = bag.__2__current; }
                else
                {
                    DropIter drop = iterator.TryCast<DropIter>();
                    if (drop != null) { kind = Kind.Dispose; current = drop.__2__current; }
                    else
                    {
                        GrabIter grab = iterator.TryCast<GrabIter>();
                        if (grab != null) { kind = Kind.Dispose; current = grab.__2__current; }
                        else
                        {
                            GrabberIter grabber = iterator.TryCast<GrabberIter>();
                            if (grabber == null) { Throttle.Warn("cleaner waits: type", "A cleaner coroutine patch ran for a class it does not know; its waits are left unchanged.", 300f); return; }
                            kind = Kind.Grabber; current = grabber.__2__current;
                        }
                    }
                }
            }
            if (current == null) return;
            WaitForSeconds wait = current.TryCast<WaitForSeconds>();
            if (wait == null) return;

            float seconds = wait.m_Seconds;
            float scale = TimeScale(kind, seconds) / Options.CleanerWorkSpeed.Value;
            if (Math.Abs(scale - 1f) < 1e-4f) return;
            float scaled = seconds * scale;
            wait.m_Seconds = scaled;
            _before[(int)kind] = seconds;
            _after[(int)kind] = scaled;
            _before[(int)Kind.Any] = seconds;
            _after[(int)Kind.Any] = scaled;
        }

        /// <summary>How much longer the option makes this wait than the game does. 1 for the game's own time.</summary>
        private static float TimeScale(Kind kind, float seconds)
        {
            switch (kind)
            {
                case Kind.Pickup:
                    return Vanilla.Active(Options.CleanerTrashPickupTime) ? Options.CleanerTrashPickupTime.Value / PickupVanilla : 1f;
                case Kind.Bag:
                    // Only the 3 s wait of the bagging is the bag time; the short waits around it keep their length.
                    return Math.Abs(seconds - BagVanilla) < 1e-3f && Vanilla.Active(Options.CleanerBagTime) ? Options.CleanerBagTime.Value / BagVanilla : 1f;
                case Kind.Dispose:
                    return Vanilla.Active(Options.CleanerDisposeTime) ? Options.CleanerDisposeTime.Value / DisposeVanilla : 1f;
                case Kind.Grabber:
                    return Options.CleanerEmptyGrabberTime.Value;
                default:
                    return 1f;
            }
        }
    }

    /// <summary>
    /// MoveNext of the five cleaner coroutines. They are separate generated classes, but their code may still be folded
    /// into one native function, so each native address is patched once (see <see cref="NativeMethods"/>) and the
    /// postfix tells the real class apart.
    /// </summary>
    [HarmonyPatch]
    internal static class Awe_CleanerWaits_Patch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var all = new List<MethodBase>();
            Add(all, typeof(PickUpIter));
            Add(all, typeof(BagIter));
            Add(all, typeof(DropIter));
            Add(all, typeof(GrabIter));
            Add(all, typeof(GrabberIter));
            return NativeMethods.OnePerAddress("cleaner MoveNext", all);
        }

        private static void Add(List<MethodBase> into, Type t)
        {
            MethodBase m = AccessTools.DeclaredMethod(t, "MoveNext", Type.EmptyTypes);
            if (m == null) { MelonLoader.MelonLogger.Warning("[Actually Working Employees] " + t.Name + ".MoveNext not found, the cleaner time options of that action have no effect."); return; }
            into.Add(m);
        }

        private static void Postfix(Il2CppSystem.Object __instance, bool __result)
        {
            if (!__result || !Net.IsServer) return;
            try { CleanerWaits.Rescale(__instance); }
            catch (Exception e) { Core.Caught("balance: cleaner wait", e); }
        }
    }
}
