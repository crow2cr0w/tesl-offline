using System;
using System.Threading;
using CcgRpc;

namespace NyxTESLOffline
{
    internal delegate void LocalDecisionProcessor(Decision decision);
    internal delegate string LocalStateSnapshot();

    /*
     * Stable seam between the retail client's protobuf decisions and the
     * authoritative alpha14 simulation.  Keeping transport and rules on
     * opposite sides of this class lets later rule modules be replaced or
     * replayed without changing the TCP protocol path.
     */
    internal static class LocalDecisionBridge
    {
        private static int _nextSequence;

        internal static void Dispatch(
            Decision decision,
            LocalStateSnapshot snapshot,
            LocalDecisionProcessor processor)
        {
            if (Object.ReferenceEquals(decision, null))
                throw new ArgumentNullException("decision");
            if (Object.ReferenceEquals(processor, null))
                throw new ArgumentNullException("processor");

            int sequence = Interlocked.Increment(ref _nextSequence);
            string before = ReadSnapshot(snapshot);
            Plugin.Log("BRIDGE IN seq=" + sequence + " "
                + DescribeDecision(decision) + " state={" + before + "}");

            try
            {
                processor(decision);
                Plugin.Log("BRIDGE OUT seq=" + sequence
                    + " state={" + ReadSnapshot(snapshot) + "}");
            }
            catch (Exception ex)
            {
                Plugin.Warn("BRIDGE ERROR seq=" + sequence
                    + " exception=" + ex.GetType().FullName
                    + " message=" + OneLine(ex.Message)
                    + " state={" + ReadSnapshot(snapshot) + "}");
                throw;
            }
        }

        private static string DescribeDecision(Decision decision)
        {
            return "uid=" + decision.CommandUid
                + " type=" + decision.Type
                + " actor=" + decision.ActorCardNetUid
                + " target=" + decision.TargetCardNetUid
                + " equip=" + decision.EquipCardNetUid
                + " lane=" + decision.LaneIndex
                + " position=" + decision.PositionIndex
                + " laneTarget=" + decision.LaneTarget;
        }

        private static string ReadSnapshot(LocalStateSnapshot snapshot)
        {
            if (Object.ReferenceEquals(snapshot, null))
                return "unavailable";

            try
            {
                return OneLine(snapshot());
            }
            catch (Exception ex)
            {
                return "snapshot-error:" + ex.GetType().Name;
            }
        }

        private static string OneLine(string value)
        {
            if (String.IsNullOrEmpty(value))
                return String.Empty;
            return value.Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
