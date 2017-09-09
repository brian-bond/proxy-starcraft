﻿using System;
using System.Collections.Generic;
using System.Linq;

using ProxyStarcraft;
using ProxyStarcraft.Proto;

namespace Sandbox
{
    /// <summary>
    /// A simple bot used to benchmark against more complex strategies.
    /// Builds as many of its basic military unit as possible and attacks.
    /// </summary>
    public class BenchmarkBot : IBot
    {
        // Every time there are this many idle soldiers, attack
        private const uint AttackThreshold = 10;

        private const uint MaxWorkersPerMineralDeposit = 2;

        private Dictionary<ulong, List<ulong>> workersByMineralDeposit;

        private bool first = true;

        // I'm not entirely sure units are getting updated with their commands in a timely fashion,
        // so I'm going to avoid issuing any commands within one step of the last command set
        private int sleep = 0;

        public IReadOnlyList<Command> Act(GameState gameState)
        {
            /* Detailed strategy:
             * 
             * 1. If there are mineral deposits near a base and not being fully mined, supply is not maxed, the base is not currently building anything, and minerals are available, build a worker.
             * 2. If minerals and supply are available, and there is a Barracks or equivalent not building a basic military unit, start building one.
             * 3. If supply is maxed out, build a Supply Depot or equivalent.
             * 4. If minerals are available, build a Barracks or equivalent.
             * 
             * No expansion. Once you hit the threshold of military units in existence, attack-move your opponent's base.
             * 
             * Implement for Terran only initially (no Creep/Pylon placement concerns).
             * 
             * Implement for maps with only two starting locations, so we don't scout.
             */
            var commands = new List<Command>();

            var controlledUnits = gameState.RawUnits.Where(u => u.Alliance == Alliance.Self).ToList();

            TerranBuilding commandCenter = null;
            var workers = new List<TerranUnit>();
            var soldiers = new List<TerranUnit>();
            var soldierProducers = new List<TerranBuilding>();

            var mineralDeposits = gameState.NeutralUnits.Where(u => u.IsMineralDeposit).ToList();


            if (sleep > 0)
            {
                sleep -= 1;
                return commands;
            }
            
            foreach (var unit in gameState.Units)
            {
                if (unit is TerranBuilding terranBuilding)
                {
                    if (terranBuilding.TerranBuildingType == TerranBuildingType.CommandCenter ||
                        terranBuilding.TerranBuildingType == TerranBuildingType.OrbitalCommand ||
                        terranBuilding.TerranBuildingType == TerranBuildingType.PlanetaryFortress)
                    {
                        commandCenter = terranBuilding;
                    }
                    else if (terranBuilding.TerranBuildingType == TerranBuildingType.Barracks)
                    {
                        soldierProducers.Add(terranBuilding);
                    }
                }
                else if (unit is TerranUnit terranUnit)
                {
                    if (terranUnit.TerranUnitType == TerranUnitType.SCV)
                    {
                        workers.Add(terranUnit);
                    }
                    else if (terranUnit.TerranUnitType == TerranUnitType.Marine)
                    {
                        soldiers.Add(terranUnit);
                    }
                }
            }

            // First update, ignore the default worker orders, which are to mass on the center mineral deposit
            // (this ends up causing problems since we have to keep track of who is harvesting what ourselves)
            if (first)
            {
                foreach (var worker in workers)
                {
                    worker.Raw.Orders.Clear();
                }

                workersByMineralDeposit = mineralDeposits.ToDictionary(m => m.Raw.Tag, m => new List<ulong>());
            }

            if (commandCenter == null)
            {
                // Accept death as inevitable.
                return new List<Command>();
                
                // TODO: Surrender?
            }

            // Each possible behavior is implemented as a function that adds a command to the list
            // if the situation is appropriate. We will then execute whichever command was added first.
            BuildWorker(gameState, commandCenter, commands);
            BuildMarine(gameState, soldierProducers, commands);
            BuildSupplyDepot(gameState, workers, soldierProducers, commands);
            BuildBarracks(gameState, workers, commands);

            commands = commands.Take(1).ToList();

            // If we tell a worker to build something, make sure we don't think he's harvesting
            if (commands.Count == 1)
            {
                sleep = 1;
                RemoveWorkerFromHarvestAssignments(commands[0].Unit);
            }

            // No matter what else happens, we can always attack, and we should always set idle workers to harvest minerals
            Attack(gameState, commandCenter, soldiers, commands);
            SetIdleWorkerToHarvest(gameState, workers, mineralDeposits, commands);

            if (first)
            {
                first = false;

                // Make sure workers don't automatically harvest minerals, since we're managing assignments ourselves
                commands.Add(commandCenter.RallyWorkers(commandCenter.Raw.Pos.X, commandCenter.Raw.Pos.Y));
            }
            
            return commands;
        }

        private void RemoveWorkerFromHarvestAssignments(Unit unit)
        {
            foreach (var pair in workersByMineralDeposit)
            {
                if (pair.Value.Contains(unit.Tag))
                {
                    pair.Value.Remove(unit.Tag);
                }
            }
        }

        private void BuildWorker(GameState gameState, TerranBuilding commandCenter, List<Command> commands)
        {
            if (commandCenter.IsBuildingSomething)
            {
                return;
            }

            if (workersByMineralDeposit.All(pair => pair.Value.Count >= 2))
            {
                return;
            }

            if (gameState.Observation.PlayerCommon.Minerals >= 50 &&
                gameState.Observation.PlayerCommon.FoodUsed < gameState.Observation.PlayerCommon.FoodCap)
            {
                commands.Add(commandCenter.Train(TerranUnitType.SCV));
            }
        }

        private void BuildMarine(GameState gameState, List<TerranBuilding> soldierProducers, List<Command> commands)
        {
            if (gameState.Observation.PlayerCommon.Minerals < 50 ||
                gameState.Observation.PlayerCommon.FoodUsed >= gameState.Observation.PlayerCommon.FoodCap)
            {
                return;
            }

            foreach (var producer in soldierProducers)
            {
                if (!producer.IsBuildingSomething && producer.Raw.BuildProgress == 1.0)
                {
                    commands.Add(producer.Train(TerranUnitType.Marine));
                    return;
                }
            }
        }

        private void Attack(GameState gameState, TerranBuilding commandCenter, List<TerranUnit> soldiers, List<Command> commands)
        {
            var idleSoldiers = soldiers.Where(s => s.Raw.Orders.Count == 0).ToList();

            if (idleSoldiers.Count >= AttackThreshold)
            {
                var enemyStartLocation = gameState.MapData.Raw.StartLocations.OrderByDescending(point => commandCenter.GetDistance(point)).First();

                foreach (var soldier in idleSoldiers)
                {
                    commands.Add(soldier.AttackMove(enemyStartLocation.X, enemyStartLocation.Y));
                }
            }
        }

        private void BuildSupplyDepot(GameState gameState, List<TerranUnit> workers, List<TerranBuilding> soldierProducers, List<Command> commands)
        {
            // Keep spare supply equal to what we could use, which is one worker from the Command Center and
            // one Marine for each Barracks
            if (gameState.Observation.PlayerCommon.FoodUsed + 1 + soldierProducers.Count >= gameState.Observation.PlayerCommon.FoodCap &&
                gameState.Observation.PlayerCommon.FoodCap < 200)
            {
                Build(gameState, workers, TerranBuildingType.SupplyDepot, 100, commands, false);
            }
        }

        private void BuildBarracks(GameState gameState, List<TerranUnit> workers, List<Command> commands)
        {
            // Can't build a Barracks if you don't have a Supply Depot first
            if (gameState.RawUnits.Any(
                unit =>
                    unit.Alliance == Alliance.Self &&
                    gameState.Translator.IsUnitOfType(unit, TerranBuildingType.SupplyDepot) &&
                    unit.BuildProgress == 1.0))
            {
                Build(gameState, workers, TerranBuildingType.Barracks, 150, commands, true);
            }
        }

        private void Build(GameState gameState, List<TerranUnit> workers, TerranBuildingType building, uint minerals, List<Command> commands, bool allowMultipleInProgress)
        {
            if (!allowMultipleInProgress && workers.Any(w => w.IsBuilding(building)))
            {
                return;
            }

            if (gameState.Observation.PlayerCommon.Minerals < minerals)
            {
                return;
            }

            var worker = workers.FirstOrDefault(w => !w.IsBuildingSomething);

            if (worker == null)
            {
                // We ran out of workers or they're all building something.
                return;
            }
            
            commands.Add(GetBuildCommand(worker, building, gameState));
        }

        private void SetIdleWorkerToHarvest(
            GameState gameState,
            List<TerranUnit> workers,
            List<Unit2> minerals,
            List<Command> commands)
        {
            var idleWorkers = workers.Where(w => w.Raw.Orders.Count == 0).ToList();
            var mineralsByTag = minerals.ToDictionary(m => m.Raw.Tag);
            
            while (!IsFullyHarvestingMineralDeposits() && idleWorkers.Count > 0)
            {
                var mineralDeposit = MineralsNeedingWorkers().First();
                var lastIdleWorker = idleWorkers.Last();

                commands.Add(lastIdleWorker.Harvest(mineralsByTag[mineralDeposit]));
                workersByMineralDeposit[mineralDeposit].Add(lastIdleWorker.Raw.Tag);
                idleWorkers.Remove(lastIdleWorker);
            }
        }

        private bool IsFullyHarvestingMineralDeposits()
        {
            return MineralsNeedingWorkers().Count == 0;
        }

        private IReadOnlyList<ulong> MineralsNeedingWorkers()
        {
            return workersByMineralDeposit.Where(pair => pair.Value.Count < MaxWorkersPerMineralDeposit).Select(pair => pair.Key).ToList();
        }
        
        private static BuildCommand GetBuildCommand(TerranUnit unit, TerranBuildingType building, GameState gameState)
        {
            // We're going to make some dumb assumptions here:
            // 1. We'd like to build this building very near where this unit currently is
            // 2. As long as we don't block anything, it doesn't matter where it goes
            var size = gameState.Translator.GetBuildingSize(building);

            var locations = new HashSet<Location> { new Location { X = (int)Math.Round(unit.Raw.Pos.X), Y = (int)Math.Round(unit.Raw.Pos.Y) } };
            var pastLocations = new HashSet<Location>();
            var nextLocations = new HashSet<Location>();

            // I think this is basically a breadth-first search of map locations
            while (locations.Count > 0)
            {
                foreach (var location in locations)
                {
                    if (gameState.MapData.CanBuild(size, location.X, location.Y))
                    {
                        return unit.Build(building, location.X, location.Y);
                    }

                    var adjacentLocations = AdjacentLocations(location, gameState.MapData.Size);

                    foreach (var adjacentLocation in adjacentLocations)
                    {
                        if (!pastLocations.Contains(adjacentLocation) && !locations.Contains(adjacentLocation) && gameState.MapData.CanTraverse(adjacentLocation))
                        {
                            nextLocations.Add(adjacentLocation);
                        }
                    }

                    pastLocations.Add(location);
                }

                locations = nextLocations;
                nextLocations = new HashSet<Location>();
            }

            throw new InvalidOperationException("Cannot find placement location anywhere on map.");
        }
        
        private static List<Location> AdjacentLocations(Location location, Size2DI mapSize)
        {
            var results = new List<Location>();

            var xVals = new List<int> { location.X - 1, location.X, location.X + 1 };
            xVals.Remove(-1);
            xVals.Remove(mapSize.X);

            var yVals = new List<int> { location.Y - 1, location.Y, location.Y + 1 };
            yVals.Remove(-1);
            yVals.Remove(mapSize.Y);

            foreach (var x in xVals)
            {
                foreach (var y in yVals)
                {
                    if (x != location.X || y != location.Y)
                    {
                        results.Add(new Location { X = x, Y = y });
                    }
                }
            }

            return results;
        }
    }
}