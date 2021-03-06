﻿using ProxyStarcraft.Map;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ProxyStarcraft.Basic
{
    public class BasicPlacementStrategy : IPlacementStrategy
    {
        public IBuildLocation GetPlacement(BuildingType building, GameState gameState)
        {
            // Obviously different rules apply to Vespene Geysers
            if (building == TerranBuildingType.Refinery ||
                building == ProtossBuildingType.Assimilator ||
                building == ZergBuildingType.Extractor)
            {
                return GetVespeneBuildingPlacement(gameState);
            }

            // Building upgrades happen in place
            if (building == TerranBuildingType.PlanetaryFortress ||
                building == TerranBuildingType.OrbitalCommand ||
                building == ZergBuildingType.Lair ||
                building == ZergBuildingType.Hive ||
                building == ZergBuildingType.LurkerDen ||
                building == ZergBuildingType.GreaterSpire)
            {
                return new UpgradeBuildLocation();
            }

            if (building == TerranBuildingType.BarracksReactor ||
                building == TerranBuildingType.BarracksTechLab ||
                building == TerranBuildingType.FactoryReactor ||
                building == TerranBuildingType.FactoryTechLab ||
                building == TerranBuildingType.StarportReactor ||
                building == TerranBuildingType.StarportTechLab)
            {
                // TODO: Make sure this is possible - will probably need to pass in the parent building or something
                return new AddOnBuildLocation();
            }

            // We're going to make some dumb assumptions here:
            // 1. We'd like to build this building very near where a main base currently is
            // 2. As long as we don't block anything, it doesn't matter where it goes
            var mainBaseLocations = GetMainBaseLocations(gameState);

            var size = gameState.Translator.GetBuildingSize(building);

            var locations = new HashSet<Location>(mainBaseLocations);

            var pastLocations = new HashSet<Location>();
            var nextLocations = new HashSet<Location>();

            // This is essentially a breadth-first search of map locations
            while (locations.Count > 0)
            {
                foreach (var location in locations)
                {
                    // TODO: Add extra check for Terran buildings with add-ons
                    // (Reactor/TechLab on Barracks/Factory/Starport), which
                    // have the potential to take up more size in the future.
                    if (gameState.MapData.CanBuild(size, location.X, location.Y))
                    {
                        return new StandardBuildLocation(location);
                    }

                    var adjacentLocations = location.AdjacentLocations(gameState.MapData.Size);

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

        private VespeneBuildLocation GetVespeneBuildingPlacement(GameState gameState)
        {
            // TODO: Just add main bases to GameState? I'm using them enough.
            var bases = gameState.Units.Where(u => u.IsMainBase).Cast<Building>().ToList();
            var vespeneBuildings = gameState.Units.Where(u => u.IsVespeneBuilding).ToList();
            var deposits = gameState.MapData.GetControlledDeposits(bases);

            // I don't know if Vespene Geysers still exist on the map once there's a building on them.

            // Better to build where there's a finished base than an in-progress one.
            var idealLocation = GetVespeneBuildingPlacement(deposits, bases, vespeneBuildings, true) ?? GetVespeneBuildingPlacement(deposits, bases, vespeneBuildings, false);
            if (idealLocation != null)
            {
                return idealLocation;
            }

            throw new InvalidOperationException();
        }

        private VespeneBuildLocation GetVespeneBuildingPlacement(List<Deposit> deposits, List<Building> bases, List<Unit> vespeneBuildings, bool baseMustBeBuilt)
        {
            // Better to build where there's a finished base than an in-progress one.
            foreach (var deposit in deposits)
            {
                var closestBase = bases.Single(b => b.GetDistance(deposit.Center) < 10f);
                if (closestBase.IsBuilt == baseMustBeBuilt)
                {
                    var vespeneGeyser = deposit.Resources.Where(
                        u => u.IsVespeneGeyser && !vespeneBuildings.Any(vb => vb.GetDistance(u) < 1f)).FirstOrDefault();

                    if (vespeneGeyser != null)
                    {
                        return new VespeneBuildLocation(vespeneGeyser);
                    }
                }
            }

            return null;
        }
        
        private List<Location> GetMainBaseLocations(GameState gameState)
        {
            var results = new List<Location>();

            foreach (var unit in gameState.Units)
            {
                if (unit.Type == TerranBuildingType.CommandCenter ||
                    unit.Type == TerranBuildingType.OrbitalCommand ||
                    unit.Type == TerranBuildingType.PlanetaryFortress ||
                    unit.Type == ProtossBuildingType.Nexus ||
                    unit.Type == ZergBuildingType.Hatchery ||
                    unit.Type == ZergBuildingType.Lair ||
                    unit.Type == ZergBuildingType.Hive)
                {
                    // They're all 5x5 so the center is 2 spaces up and to the right of (X, Y) as we're marking it
                    results.Add(new Location { X = (int)unit.X + 2, Y = (int)unit.Y + 2 });
                }
            }

            return results;
        }
    }
}
