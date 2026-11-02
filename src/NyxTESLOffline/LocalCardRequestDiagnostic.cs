using System;
using System.Collections;
using System.IO;
using System.Reflection;
using CcgRpc;
using HarmonyLib;
using SPTESL;
using UnityEngine;

namespace NyxTESLOffline
{
    // Diagnostic only: uses the existing retail connection, never another socket.
    [HarmonyPatch(typeof(Plugin), "Update")]
    internal static class LocalCardRequestDiagnostic
    {
        private static float _nextPoll;
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        [HarmonyPostfix]
        internal static void Update()
        {
            if (Plugin.Instance == null || !(bool)Field(Plugin.Instance, "_offlineReady")) return;
            bool keyboard = Input.GetKeyDown(KeyCode.F9);
            if (!keyboard && Time.realtimeSinceStartup < _nextPoll) return;
            _nextPoll = Time.realtimeSinceStartup + 1f;
            string path = Path.Combine(Path.Combine(Application.dataPath,
                "Managed/NyxTESLOffline-data"), "diagnostic-play.request");
            if (!keyboard && !File.Exists(path)) return;
            try
            {
                int cardId = 0;
                if (!keyboard)
                {
                    string request = File.ReadAllText(path).Trim();
                    // Consume before sending: a failed request is never retried automatically.
                    File.Delete(path);
                    if (request != "first" && (!Int32.TryParse(request, out cardId) || cardId == 0))
                        throw new InvalidOperationException("Expected first or a nonzero card ID");
                }
                Send(cardId);
            }
            catch (Exception ex)
            {
                Plugin.Warn("DIAGNOSTIC PLAY refused: " + ex.GetType().Name + " " + ex.Message);
            }
        }

        private static object Field(object value, string name)
        {
            return value.GetType().GetField(name, Fields).GetValue(value);
        }

        private static void Send(int requestedCardId)
        {
            var manager = GameNetworkManager.Instance;
            if (manager == null || manager.IsOutsideOfMatch() || manager.IsSpectating())
                throw new InvalidOperationException("No active local player match");
            var tcp = (TcpNetworkClient)AccessTools.Field(typeof(GameNetworkManager), "_tcpClient").GetValue(manager);
            if (tcp == null || !tcp.IsConnected())
                throw new InvalidOperationException("Retail TCP client is disconnected");
            var asyncState = AccessTools.Field(typeof(TcpNetworkClient), "_async").GetValue(tcp);
            var bundle = Field(asyncState, "Bundle");
            var socket = (System.Net.Sockets.TcpClient)Field(bundle, "Client");
            var remote = socket.Client.RemoteEndPoint as System.Net.IPEndPoint;
            if (remote == null || !System.Net.IPAddress.IsLoopback(remote.Address) || remote.Port != 4001)
                throw new InvalidOperationException("Diagnostic transport must be loopback port 4001");
            var states = (IDictionary)AccessTools.Field(typeof(OfflineSimulationHost), "States").GetValue(null);
            if (states.Count != 1) throw new InvalidOperationException("Expected exactly one local host");
            object state = null;
            foreach (DictionaryEntry entry in states) state = entry.Value;
            var simulation = (Simulation)Field(state, "Simulation");
            int playerIndex = (int)Field(state, "ActivePlayerIndex");
            if (!(bool)Field(state, "GameInitialized") || (bool)Field(state, "GameplayFaulted") ||
                !simulation.Players[playerIndex].IsHuman ||
                simulation.Players[playerIndex].NetId.Id != manager.GetLocalPlayerNetId().Id)
                throw new InvalidOperationException("Not the ready local player's turn");
            var gameState = AccessTools.Method(typeof(OfflineSimulationHost), "GetGameState").Invoke(null, new object[] { simulation });
            var phase = gameState.GetType().GetField("GamePhase", Fields);
            object phaseValue = phase != null ? phase.GetValue(gameState) :
                gameState.GetType().GetProperty("GamePhase").GetValue(gameState, null);
            if (phaseValue.ToString() != "MainLoop" ||
                Field(state, "PendingCustomAction").ToString() != "None" ||
                (int)Field(state, "PendingProphecyPlayerIndex") >= 0)
                throw new InvalidOperationException("Pending choice, prophecy or non-play phase");
            var queues = (ServerEffectUtilities.TriggerQueues)Field(state, "TriggerQueues");
            if (queues.Active.Count != 0) throw new InvalidOperationException("Pending triggered effect");
            int preferred = 0;
            if (requestedCardId != 0)
            {
                var cards = simulation.CardCatalog.GatherCardsInZone(
                    delegate(ServerCard card) { return card.CardId == requestedCardId; },
                    simulation.Players[playerIndex].NetId, GameZoneName.Hand);
                if (cards.Count == 0) throw new InvalidOperationException("Requested card is not in hand");
                preferred = cards[0].NetUid.Id;
            }
            MethodInfo build = null;
            foreach (var method in typeof(OfflineSimulationHost).GetMethods(BindingFlags.Static | BindingFlags.NonPublic))
                if (method.Name == "BuildFirstPlayableDecision" && method.GetParameters().Length == 6) build = method;
            object[] args = { state, playerIndex, GameZoneName.Hand, false, preferred, null };
            if (!(bool)build.Invoke(null, args)) throw new InvalidOperationException("No legal requested play");
            var decision = (Decision)args[5];
            if (preferred != 0 && decision.ActorCardNetUid != preferred)
                throw new InvalidOperationException("Requested card was not selected");
            // SendDecision preserves retail framing, encryption and response timing.
            AccessTools.Method(typeof(GameNetworkManager), "SendDecision").Invoke(manager, new object[] { decision });
            Plugin.Log("DIAGNOSTIC PLAY submitted via retail TCP type=20 actor=" +
                decision.ActorCardNetUid + " command=" + decision.CommandUid +
                " lane=" + decision.LaneIndex + "; awaiting BRIDGE IN/OUT");
        }
    }
}
