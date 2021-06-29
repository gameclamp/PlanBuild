﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlanBuild.Blueprints
{
    internal class BlueprintSync
    {
        private static Action<bool, string> OnAnswerReceived;

        public static void Init()
        {
            GetLocalBlueprints();
            On.Game.Start += RegisterRPC;
            //On.ZNet.SendPeerInfo += InitServerBlueprints;
            On.ZNet.OnDestroy += ResetServerBlueprints;
        }

        private static void RegisterRPC(On.Game.orig_Start orig, Game self)
        {
            orig(self);
            ZRoutedRpc.instance.Register(nameof(RPC_PlanBuild_GetServerBlueprints), new Action<long, ZPackage>(RPC_PlanBuild_GetServerBlueprints));
            ZRoutedRpc.instance.Register(nameof(RPC_PlanBuild_PushBlueprint), new Action<long, ZPackage>(RPC_PlanBuild_PushBlueprint));
        }

        private static void InitServerBlueprints(On.ZNet.orig_SendPeerInfo orig, ZNet self, ZRpc rpc, string password)
        {
            orig(self, rpc, password);
            GetServerBlueprints(null);
        }

        private static void ResetServerBlueprints(On.ZNet.orig_OnDestroy orig, ZNet self)
        {
            BlueprintManager.ServerBlueprints?.Clear();
            orig(self);
        }

        internal static void GetLocalBlueprints()
        {
            Jotunn.Logger.LogMessage("Loading known blueprints");

            if (!Directory.Exists(BlueprintConfig.blueprintSaveDirectoryConfig.Value))
            {
                Directory.CreateDirectory(BlueprintConfig.blueprintSaveDirectoryConfig.Value);
            }

            List<string> blueprintFiles = new List<string>();
            blueprintFiles.AddRange(Directory.EnumerateFiles(BlueprintConfig.blueprintSearchDirectoryConfig.Value, "*.blueprint", SearchOption.AllDirectories));
            blueprintFiles.AddRange(Directory.EnumerateFiles(BlueprintConfig.blueprintSearchDirectoryConfig.Value, "*.vbuild", SearchOption.AllDirectories));

            blueprintFiles = blueprintFiles.Select(absolute => absolute.Replace(BepInEx.Paths.BepInExRootPath, null)).ToList();

            // Try to load all saved blueprints
            foreach (var relativeFilePath in blueprintFiles)
            {
                try
                {
                    string id = Path.GetFileNameWithoutExtension(relativeFilePath);
                    if (!BlueprintManager.LocalBlueprints.ContainsKey(id))
                    {
                        Blueprint bp = Blueprint.FromFile(relativeFilePath);
                        BlueprintManager.LocalBlueprints.Add(bp.ID, bp);
                    }
                }
                catch (Exception ex)
                {
                    Jotunn.Logger.LogWarning($"Could not load blueprint {relativeFilePath}: {ex}");
                }
            }
        }

        internal static bool SaveLocalBlueprint(string id)
        {
            if (BlueprintManager.LocalBlueprints == null)
            {
                return false;
            }
            if (!BlueprintManager.LocalBlueprints.TryGetValue(id, out var blueprint))
            {
                return false;
            }

            Jotunn.Logger.LogMessage($"Saving local blueprint {id}");

            return blueprint.ToFile();
        }

        /// <summary>
        ///     When connected to a server, register a callback and invoke the RPC for uploading 
        ///     a local blueprint to the server directory.
        /// </summary>
        /// <param name="id">ID of the blueprint</param>
        /// <param name="callback">Is called after the server responded</param>
        internal static void PushBlueprint(string id, Action<bool, string> callback)
        {
            if (!BlueprintConfig.allowServerBlueprints.Value)
            {
                callback?.Invoke(false, "Server blueprints disabled");
            }
            if (ZNet.instance != null && !ZNet.instance.IsServer() && ZNet.m_connectionStatus == ZNet.ConnectionStatus.Connected)
            {
                if (BlueprintManager.LocalBlueprints.TryGetValue(id, out var blueprint))
                {
                    Jotunn.Logger.LogMessage($"Sending blueprint {id} to server");
                    OnAnswerReceived += callback;
                    ZRoutedRpc.instance.InvokeRoutedRPC(nameof(RPC_PlanBuild_PushBlueprint), blueprint.ToZPackage());
                }
            }
            else
            {
                callback?.Invoke(false, "Not connected");
            }
        }

        /// <summary>
        ///     RPC method for pushing blueprints to the server.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="pkg"></param>
        private static void RPC_PlanBuild_PushBlueprint(long sender, ZPackage pkg)
        {
            // Globally disabled
            if (!BlueprintConfig.allowServerBlueprints.Value)
            {
                return;
            }
            // Server receive (local game and dedicated)
            if (ZNet.instance.IsServer())
            {
                var peer = ZNet.instance.m_peers.FirstOrDefault(x => x.m_uid == sender);
                if (peer != null)
                {
                    Jotunn.Logger.LogDebug($"Received blueprint from peer #{sender}");

                    // Deserialize blueprint
                    bool success = true;
                    string message = string.Empty;
                    try
                    {
                        Blueprint bp = Blueprint.FromZPackage(pkg);
                        if (BlueprintManager.LocalBlueprints.ContainsKey(bp.ID))
                        {
                            throw new Exception($"Blueprint ID {bp.ID} already exists on this server");
                        }
                        if (!bp.ToFile())
                        {
                            throw new Exception("Could not save blueprint");
                        }
                        BlueprintManager.LocalBlueprints.Add(bp.ID, bp);
                        message = bp.ID;
                    }
                    catch (Exception ex)
                    {
                        success = false;
                        message = ex.Message;
                    }
                    
                    // Invoke answer response
                    ZPackage package = new ZPackage();
                    package.Write(success);
                    package.Write(message);
                    ZRoutedRpc.instance.InvokeRoutedRPC(sender, nameof(RPC_PlanBuild_PushBlueprint), package);
                }
            }
            // Client receive
            else
            {
                if (pkg != null && pkg.Size() > 0 && sender == ZNet.instance.GetServerPeer().m_uid)
                {
                    Jotunn.Logger.LogDebug($"Received push answer from server");

                    // Check answer
                    bool success = pkg.ReadBool();
                    string message = pkg.ReadString();
                    try
                    {
                        if (success)
                        {
                            if (!BlueprintManager.ServerBlueprints.ContainsKey(message))
                            {
                                BlueprintManager.LocalBlueprints.TryGetValue(message, out var bp);
                                BlueprintManager.ServerBlueprints.Add(bp.ID, bp);
                            }
                        }
                    }
                    finally
                    {
                        OnAnswerReceived?.Invoke(success, message);
                        OnAnswerReceived = null;
                    }
                }
            }
        }

        /// <summary>
        ///     When connected to a server clear current server list, register callback to the delegate and finally invoke the RPC.<br />
        ///     Per default the server list gets cached after the first load. Set useCache to false to force a refresh from the server.
        /// </summary>
        /// <param name="callback">Delegate method which gets called when the server list was received</param>
        /// <param name="useCache">Return the internal cached list after loading, defaults to true</param>
        internal static void GetServerBlueprints(Action<bool, string> callback, bool useCache = true)
        {
            if (!BlueprintConfig.allowServerBlueprints.Value)
            {
                callback?.Invoke(false, "Server blueprints disabled");
            }
            if (ZNet.instance != null && !ZNet.instance.IsServer() && ZNet.m_connectionStatus == ZNet.ConnectionStatus.Connected)
            {
                if (useCache && BlueprintManager.ServerBlueprints.Count() > 0)
                {
                    Jotunn.Logger.LogMessage("Getting server blueprint list from cache");
                    callback?.Invoke(true, string.Empty);
                }
                else
                {
                    Jotunn.Logger.LogMessage("Requesting server blueprint list");
                    OnAnswerReceived += callback;
                    ZRoutedRpc.instance.InvokeRoutedRPC(nameof(RPC_PlanBuild_GetServerBlueprints), new ZPackage());
                }
            }
            else
            {
                callback?.Invoke(false, "Not connected");
            }
        }

        /// <summary>
        ///     RPC method for sending / receiving the actual blueprint lists.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="pkg"></param>
        private static void RPC_PlanBuild_GetServerBlueprints(long sender, ZPackage pkg)
        {
            // Globally disabled
            if (!BlueprintConfig.allowServerBlueprints.Value)
            {
                return;
            }
            // Server receive (local game and dedicated)
            if (ZNet.instance.IsServer())
            {
                // Validate peer
                var peer = ZNet.instance.m_peers.FirstOrDefault(x => x.m_uid == sender);
                if (peer != null)
                {
                    Jotunn.Logger.LogDebug($"Sending blueprint data to peer #{sender}");

                    // Reload and send current blueprint list in BlueprintManager back to the original sender
                    GetLocalBlueprints();
                    ZRoutedRpc.instance.InvokeRoutedRPC(
                        sender, nameof(RPC_PlanBuild_GetServerBlueprints), BlueprintManager.LocalBlueprints.ToZPackage());
                }
            }
            // Client receive
            else
            {
                // Validate the message is from the server and not another client.
                if (pkg != null && pkg.Size() > 0 && sender == ZNet.instance.GetServerPeer().m_uid)
                {
                    Jotunn.Logger.LogDebug("Received blueprints from server");

                    // Deserialize list, call delegates and finally clear delegates
                    bool success = true;
                    string message = string.Empty;
                    try
                    {
                        BlueprintManager.ServerBlueprints.Clear();
                        BlueprintManager.ServerBlueprints = BlueprintDictionary.FromZPackage(pkg, BlueprintLocation.Server);
                    }
                    catch (Exception ex)
                    {
                        success = false;
                        message = ex.Message;
                    }
                    finally
                    {
                        OnAnswerReceived?.Invoke(success, message);
                        OnAnswerReceived = null;
                    }
                }
            }
        }

        internal static void SaveServerBlueprint(string id, Action<bool, string> callback)
        {
            if (!BlueprintConfig.allowServerBlueprints.Value)
            {
                callback?.Invoke(false, "Server blueprints disabled");
            }
            if (ZNet.instance != null && !ZNet.instance.IsServer() && ZNet.m_connectionStatus == ZNet.ConnectionStatus.Connected)
            {
                if (BlueprintManager.ServerBlueprints.TryGetValue(id, out var blueprint))
                {
                    Jotunn.Logger.LogMessage($"Saving blueprint {id} on server");
                    OnAnswerReceived += callback;
                    ZRoutedRpc.instance.InvokeRoutedRPC(nameof(RPC_PlanBuild_PushBlueprint), blueprint.ToZPackage());
                }
            }
            else
            {
                callback?.Invoke(false, "Not connected");
            }
        }

        /// <summary>
        ///     Save a blueprint from the internal server list as a local blueprint and add it to the <see cref="BlueprintManager"/>.
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        internal static bool PullBlueprint(string id)
        {
            if (!BlueprintConfig.allowServerBlueprints.Value)
            {
                return false;
            }
            if (BlueprintManager.ServerBlueprints == null)
            {
                return false;
            }
            if (!BlueprintManager.ServerBlueprints.TryGetValue(id, out var bp))
            {
                return false;
            }

            Jotunn.Logger.LogDebug($"Saving server blueprint {id}");

            if (BlueprintManager.LocalBlueprints.ContainsKey(id))
            {
                BlueprintManager.LocalBlueprints[id].Destroy();
                BlueprintManager.LocalBlueprints.Remove(id);
            }

            bp.ToFile();
            bp.CreatePrefab();
            Player.m_localPlayer.UpdateKnownRecipesList();
            BlueprintManager.LocalBlueprints.Add(bp.ID, bp);

            return true;
        }
    }
}