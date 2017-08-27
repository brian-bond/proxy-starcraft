﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

using ProxyStarcraft.Proto;
using WebSocket4Net;
using System.Linq;
using System.Diagnostics;

namespace ProxyStarcraft.Client
{
    /// <summary>
    /// Code to simplify dealing with the API by making it appear synchronous.
    /// 
    /// Probably basically harmless to use this when running the game in Single-Step mode,
    /// but maybe less so in Real-Time mode. Naturally this is not thread-safe.
    /// </summary>
    public class SynchronousApiClient : IDisposable
    {
        // Unique action IDs. May change. Should get from Abilities dictionary instead.
        private const int Move = 16;
        private const int Attack = 23;

        private static object socketLock = new object();

        private WebSocket webSocket;

        private bool connected = false;
        private bool waiting = false;
        private int connectionRetries = 4;

        private Response socketResponse;

        // Note: these only potentially changes at the beginning of a game, so we will avoid calling for them repeatedly.
        private ResponseGameInfo gameInfo;
        private Dictionary<uint, UnitTypeData> unitTypes;
        private Dictionary<uint, AbilityData> abilities;
        private MapData mapData;

        private Translator translator;

        private Request lastRequest;

        public SynchronousApiClient(String address)
        {
            webSocket = new WebSocket(address);

            webSocket.DataReceived += OnReceivedData;
            webSocket.MessageReceived += OnReceivedMessage;
            webSocket.Opened += OnSocketOpened;
            webSocket.Error += OnSocketError;
        }

        public GameState GetGameState()
        {
            gameInfo = gameInfo ?? Call(new Request { GameInfo = new RequestGameInfo() }).GameInfo;
            unitTypes = unitTypes ?? Call(new Request { Data = new RequestData { UnitTypeId = true } }).Data.Units.ToDictionary(unitType => unitType.UnitId);
            abilities = abilities ?? Call(new Request { Data = new RequestData { AbilityId = true } }).Data.Abilities.ToDictionary(ability => ability.AbilityId);

            translator = translator ?? new Translator(abilities, unitTypes);

            mapData = mapData ?? new MapData(gameInfo.StartRaw);
            
            var response = Call(new Request { Observation = new RequestObservation() });

            mapData = new MapData(mapData, response.Observation.Observation.RawData.Units, translator, unitTypes);
            
            return new GameState(gameInfo, response.Observation, mapData, unitTypes, abilities, translator);
        }

        public List<uint> GetAbilities(ulong unitTag)
        {
            var queryRequest = new Request { Query = new RequestQuery { } };

            queryRequest.Query.Abilities.Add(new RequestQueryAvailableAbilities { UnitTag = unitTag });

            var response = Call(queryRequest);

            return response.Query.Abilities[0].Abilities.Select(a => (uint)a.AbilityId).ToList();
        }

        public void SendCommands(IEnumerable<ICommand> commands)
        {
            var actionRequest = new Request { Action = new RequestAction() };

            foreach (var command in commands)
            {
                actionRequest.Action.Actions.Add(BuildAction(command));
            }

            // TODO: Check response for errors
            var actionResponse = Call(actionRequest);

            if (actionResponse.Action.Result.Any(result => result != ActionResult.Success))
            {
                Debugger.Break();
            }
        }

        private Proto.Action BuildAction(ICommand command)
        {
            ActionRawUnitCommand unitCommand;

            switch (command)
            {
                case MoveCommand moveCommand:
                    unitCommand = new ActionRawUnitCommand { AbilityId = Move, TargetWorldSpacePos = new Point2D { X = moveCommand.X, Y = moveCommand.Y } };
                    break;
                case AttackMoveCommand attackMoveCommand:
                    unitCommand = new ActionRawUnitCommand { AbilityId = Attack, TargetWorldSpacePos = new Point2D { X = attackMoveCommand.X, Y = attackMoveCommand.Y } };
                    break;
                case AttackCommand attackCommand:
                    unitCommand = new ActionRawUnitCommand { AbilityId = Attack, TargetUnitTag = attackCommand.Target.Tag };
                    break;
                case BuildCommand buildCommand:
                    var buildAbilityId = translator.GetAbilityId(buildCommand);
                    var buildingSize = translator.GetBuildingSize(buildCommand);
                    var x = buildCommand.X + buildingSize.X * 0.5f;
                    var y = buildCommand.Y + buildingSize.Y * 0.5f;
                    unitCommand = new ActionRawUnitCommand { AbilityId = (int)buildAbilityId, TargetWorldSpacePos = new Point2D { X = x, Y = y } };
                    break;
                case TrainCommand trainCommand:
                    var trainAbilityId = translator.GetAbilityId(trainCommand);
                    unitCommand = new ActionRawUnitCommand { AbilityId = (int)trainAbilityId };
                    break;
                case HarvestCommand harvestCommand:
                    var harvestAbilityId = translator.GetHarvestAbility(harvestCommand.Unit);
                    unitCommand = new ActionRawUnitCommand { AbilityId = (int)harvestAbilityId, TargetUnitTag = harvestCommand.Target.Tag };
                    break;
                case RallyLocationCommand rallyCommand:
                    var rallyAbilityId = translator.GetRallyAbility(rallyCommand.Unit);
                    unitCommand = new ActionRawUnitCommand { AbilityId = (int)rallyAbilityId, TargetWorldSpacePos = new Point2D { X = rallyCommand.X, Y = rallyCommand.Y } };
                    break;
                case RallyWorkersLocationCommand rallyWorkersCommand:
                    var rallyWorkersAbilityId = translator.GetRallyWorkersAbility(rallyWorkersCommand.Unit);
                    unitCommand = new ActionRawUnitCommand { AbilityId = (int)rallyWorkersAbilityId, TargetWorldSpacePos = new Point2D { X = rallyWorkersCommand.X, Y = rallyWorkersCommand.Y } };
                    break;
                case RallyTargetCommand rallyTargetCommand:
                    var rallyTargetAbilityId = translator.GetRallyAbility(rallyTargetCommand.Unit);
                    unitCommand = new ActionRawUnitCommand { AbilityId = (int)rallyTargetAbilityId, TargetUnitTag = rallyTargetCommand.Target.Tag };
                    break;
                case RallyWorkersTargetCommand rallyWorkersTargetCommand:
                    var rallyWorkersTargetAbilityId = translator.GetRallyWorkersAbility(rallyWorkersTargetCommand.Unit);
                    unitCommand = new ActionRawUnitCommand { AbilityId = (int)rallyWorkersTargetAbilityId, TargetUnitTag = rallyWorkersTargetCommand.Target.Tag };
                    break;
                default:
                    throw new NotImplementedException();
            }
            
            unitCommand.UnitTags.Add(command.Unit.Tag);
            return new Proto.Action { ActionRaw = new ActionRaw { UnitCommand = unitCommand } };
        }
        
        public void Step()
        {
            Step(1);
        }

        public void Step(uint stepCount)
        {
            Call(new Request { Step = new RequestStep { Count = stepCount } });
        }

        public void LeaveGame()
        {
            Call(new Request { LeaveGame = new RequestLeaveGame() });
            gameInfo = null;
            unitTypes = null;
            abilities = null;
            translator = null;
        }

        public bool InitiateSinglePlayerGame(string map, Race race)
        {
            var createGameRequest = new Request
            {
                CreateGame = new RequestCreateGame
                {
                    LocalMap = new LocalMap { MapPath = map }
                }
            };

            createGameRequest.CreateGame.PlayerSetup.Add(
                new PlayerSetup
                {
                    Type = PlayerType.Participant,
                    Race = race
                });

            var createGameResponse = Call(createGameRequest);

            if (createGameResponse.Status != Status.InitGame)
            {
                return false;
            }

            var joinGameResponse = Call(
                new Request
                {
                    JoinGame = new RequestJoinGame
                    {
                        Race = race,
                        Options = new InterfaceOptions { Raw = true }
                    }
                });

            return joinGameResponse.Status == Status.InGame;
        }

        public bool InitiateGameAgainstComputer(string map, Race race, Difficulty opponentLevel)
        {
            var createGameRequest = new Request
            {
                CreateGame = new RequestCreateGame
                {
                    LocalMap = new LocalMap { MapPath = map }
                }
            };

            createGameRequest.CreateGame.PlayerSetup.Add(
                new PlayerSetup
                {
                    Type = PlayerType.Participant,
                    Race = race
                });
            createGameRequest.CreateGame.PlayerSetup.Add(
                new PlayerSetup
                {
                    Type = PlayerType.Computer,
                    Difficulty = opponentLevel,
                    Race = Race.Random
                });

            var createGameResponse = Call(createGameRequest);

            if (createGameResponse.Status != Status.InitGame)
            {
                return false;
            }

            var joinGameResponse = Call(
                new Request
                {
                    JoinGame = new RequestJoinGame
                    {
                        Race = race,
                        Options = new InterfaceOptions { Raw = true }
                    }
                });

            return joinGameResponse.Status == Status.InGame;
        }

        public Response Call(Request request)
        {
            Connect();

            SendRequest(request);

            // TODO: Use a proper synchronization primitive for this
            while (waiting)
            {
                for (var i = 0; i < 100; i++)
                {
                    Thread.Sleep(5);
                    if (!waiting)
                    {
                        break;
                    }
                }

                // Most of our requests are actually idempotent, so on the off chance
                // that we never get a response, it makes sense to just resend them.
                // This might be something that should change if I can diagnose the root cause.
                if (waiting)
                {
                    SendRequest(request);
                }
            }

            lock(socketLock)
            {
                var retval = socketResponse;
                socketResponse = null;
                return retval;
            }
        }

        private void SendRequest(Request request)
        {
            using (var mem = new MemoryStream())
            {
                using (var stream = new Google.Protobuf.CodedOutputStream(mem))
                {
                    request.WriteTo(stream);
                }

                var data = mem.ToArray();

                lock (socketLock)
                {
                    lastRequest = request;
                    waiting = true;
                    webSocket.Send(data, 0, data.Length);
                }
            }
        }

        private void Connect()
        {
            if (connected)
            {
                return;
            }

            webSocket.Open();

            while (!connected)
            {
                Thread.Sleep(20);
            }
        }

        private void OnReceivedData(object sender, WebSocket4Net.DataReceivedEventArgs e)
        {
            lock (socketLock)
            {
                socketResponse = Response.Parser.ParseFrom(e.Data);
                waiting = false;
            }
        }

        private void OnReceivedMessage(object sender, MessageReceivedEventArgs e)
        {
            throw new NotImplementedException("Expecting DataReceived rather than MessageReceived from WebSocket client.");
        }

        private void OnSocketOpened(object sender, EventArgs e)
        {
            connected = true;
            connectionRetries = 5;
        }

        private void OnSocketError(object sender, SuperSocket.ClientEngine.ErrorEventArgs e)
        {
            lock(socketLock)
            {
                if (connectionRetries > 0)
                {
                    if (!connected)
                    {
                        Thread.Sleep(5000);
                    }
                    
                    connectionRetries -= 1;

                    try
                    {
                        webSocket.Open();

                        if (waiting && lastRequest != null)
                        {
                            SendRequest(lastRequest);
                        }
                    }
                    catch (Exception)
                    {
                    }
                    
                    return;
                }
            }
            
            throw new Exception("Unexpected socket error.", e.Exception);
        }
        
        #region IDisposable Support
        private bool disposed = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
                    webSocket.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposed = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~SynchronousApiClient() {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }
        #endregion
    }
}
