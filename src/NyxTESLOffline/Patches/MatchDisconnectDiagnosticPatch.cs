using System;
using System.Reflection;
using HarmonyLib;
using SPTESL;
using CcgRpc;

namespace NyxTESLOffline.Patches
{
    /* Diagnostic only.  The retail popup does not say whether StopMatch was
     * caused by a RejectReason, the 60 s connection timer, or an explicit
     * network-failure path.  Log the exact state before retail teardown. */
    [HarmonyPatch(typeof(GameNetworkManager), "StopMatch")]
    internal static class MatchDisconnectDiagnosticPatch
    {
        private static readonly FieldInfo RejectReasonField =
            AccessTools.Field(typeof(GameNetworkManager), "_rejectReason");
        private static readonly FieldInfo HostServerField =
            AccessTools.Field(typeof(GameNetworkManager), "_hostServer");
        private static readonly FieldInfo TcpClientField =
            AccessTools.Field(typeof(GameNetworkManager), "_tcpClient");
        private static readonly FieldInfo EmbeddedServerField =
            AccessTools.Field(typeof(GameNetworkManager), "_embeddedMatchServer");

        private static void Prefix(GameNetworkManager __instance, bool dueToNetworkIssues)
        {
            try
            {
                RejectReason reject = RejectReason.RejectNone;
                if (!Object.ReferenceEquals(RejectReasonField, null))
                    reject = (RejectReason)RejectReasonField.GetValue(__instance);

                object host = Object.ReferenceEquals(HostServerField, null)
                    ? null : HostServerField.GetValue(__instance);
                TcpNetworkClient client = Object.ReferenceEquals(TcpClientField, null)
                    ? null : (TcpNetworkClient)TcpClientField.GetValue(__instance);
                Simulation embedded = Object.ReferenceEquals(EmbeddedServerField, null)
                    ? null : (Simulation)EmbeddedServerField.GetValue(__instance);

                string clientState = Object.ReferenceEquals(client, null)
                    ? "null"
                    : ("connected=" + client.IsConnected()
                       + " valid=" + client.HasValidConnection()
                       + " connecting=" + client.IsConnecting());

                Plugin.Warn("GameNetworkManager.StopMatch: dueToNetworkIssues="
                    + dueToNetworkIssues
                    + " reject=" + reject
                    + " host=" + (Object.ReferenceEquals(host, null) ? "null" : host.ToString())
                    + " tcpClient={" + clientState + "}"
                    + " embeddedServer=" + (!Object.ReferenceEquals(embedded, null)));
            }
            catch (Exception ex)
            {
                Plugin.Warn("StopMatch diagnostic failed: " + ex);
            }
        }
    }
}
