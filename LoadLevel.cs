﻿using MonoMod;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

class patch_LoadLevel : LoadLevel
{
    [MonoModIgnore]
    private static bool randomize;
    [MonoModIgnore]
    private static int customLevelPalette;
    public static Dictionary<string, int> oldFMODIndex = FMODSquare.AreaNameToFMODIndex;

    private static IEnumerable<string> GetFiles(string dir, string pattern)
    {
        return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories);
    }
    private static IEnumerable<string[]> GetTokens(string dir, string name)
    {
        return from file in GetFiles(dir, name + ".txt")
               from line in ReadTextFile(file).Replace("\r", "").Split('\n')
               select line.Split(' ');
    }

    [MonoModIgnore]
    private static Dictionary<string, List<LoadLevel.UnlockableWall>> unlockableWalls;
    private static void LastMinuteHubFixes()
    {
        var music = FMODSquare.AreaNameToFMODIndex;
        music["Area_Intro"] = music[FindPlayerBlock().OuterLevel.hubAreaName];

        // Fix some things in DoHubModifications that were just too complicated to do in assembly.
        foreach (var floor in floors)
        {
            // In areas which precede areas without unlockable walls, main-path portals with no lines
            // should not be given a line out of the level, because there's no previous level for
            // them to be connected to.
            if (floor.Type == Floor.FloorType.LevelPortal
                && Hub.puzzleLineRefs[floor.SceneName].toMe.Count == 0)
            {
                var outside = floor.OuterLevel.GetExitBlock().OuterLevel;
                if (outside == null || !(from block in blocks
                                        where block.unlockerScene != null
                                        from floor2 in outside.floorList
                                        where floor2.SceneName == block.unlockerScene
                                        select floor2).Any())
                {
                    floor.UnlockLines = floor.UnlockLines.Where(
                        line => floor.ypos + line.dy != floor.OuterLevel.height
                        ).ToArray();
                }
            }

            // Add a line from the final level to the credits
            if (floor.Type == Floor.FloorType.LevelPortal
                && Hub.puzzleLineRefs[floor.SceneName].fromMe.Count == 0
                && floor.Hard == 0)
            {
                foreach (var floor2 in floor.OuterLevel.floorList)
                {
                    if (floor2.Type == Floor.FloorType.PlayerButton)
                    {
                        var unlockLine = new Floor.UnlockLine
                        {
                            dx = floor2.xpos - floor.xpos,
                            dy = floor2.ypos - floor.ypos,
                            lastPortal = true,
                            lit = floor.Won,
                            thick = true
                        };
                        Floor.ComputeUnlockLineShape(floor, floor2, unlockLine, false);
                        floor.UnlockLines = floor.UnlockLines.Append(unlockLine).ToArray();
                    }
                }
            }
        }
    }

    private static void LastMinuteLevelFixes()
    {
        // Fix palette, which was set to the one in puzzledata
        if (!randomize)
        {
            if (customLevelPalette >= 0)
            {
                ApplyPalette(customLevelPalette);
            }
            else
            {
                Draw.Palette = 0;
                foreach (var block in blocks)
                {
                    block.hue = block.startHue;
                    block.sat = block.startSat;
                    block.val = block.startVal;
                }
                ComputeBorderColors();
            }
        }
        // ...and the music, which was also set to the one in puzzledata.
        // It is not possible to play *no* music.
        Hub.puzzleData[currentLevelName].musicArea = customLevelMusic;

        if (InHub()) LastMinuteHubFixes();
    }
    public static void LoadImages(string dir)
    {
        foreach (string file in GetFiles(dir, "*"))
        {
            if (Path.GetExtension(file) == ".txt") continue;
            string name = Path.GetFileNameWithoutExtension(file);
            if (Hub.puzzleData.ContainsKey(name)
                && Hub.puzzleData[name].thumbnail == null)
            {
                var tex = new Texture2D(0, 0);
                tex.LoadImage(File.ReadAllBytes(file));
                Hub.puzzleData[name].thumbnail = tex;
            }
        }
    }

    extern public static void orig_Load(string data);
    new public static void Load(string data)
    {
        if (currentLevelName != "custom_level" ||
            Path.GetFileName(lastLoadedCustomLevelPath) != "hub.txt")
        {
            orig_Load(data);
            if (patch_World.inCustomHub) LastMinuteLevelFixes();
            return;
        }

        // I mean, it's not loaded *yet*.
        // This encourages the game to reload a couple of things.
        hubLoaded = false;

        patch_World.inCustomHub = true;
        currentLevelName = "hub";
        Hub.puzzleData.Clear();
        patch_World.paths.Clear();
        string dir = Path.GetDirectoryName(lastLoadedCustomLevelPath);
        patch_World.customHubDir = dir;

        var music = FMODSquare.AreaNameToFMODIndex = new Dictionary<string, int>();
        music["Menu_Paused"] = 20;
        music["Menu_Credits"] = 21;
        foreach (string[] tokens in GetTokens(dir, "area_data"))
        {
            string name = tokens[0];
            music[name] = int.Parse(tokens[1]);
        }

        var puzzleNames = new List<string>();
        foreach (string[] tokens in GetTokens(dir, "puzzle_data"))
        {
            Hub.puzzleData[tokens[0]] = new Hub.PuzzleData
            {
                hard = int.Parse(tokens[1]),
                eyesJump = Hub.ParseBool(tokens[2]),
                referenceName = tokens[3],
                musicArea = -1
            };
            puzzleNames.Add(tokens[0]);
        }
        Hub.puzzleData["hub"] = new Hub.PuzzleData();
        Hub.puzzleNamesSorted = puzzleNames.ToArray();

        foreach (string file in GetFiles(dir, "*.txt"))
        {
            patch_World.paths[Path.GetFileNameWithoutExtension(file)] = file;
        }
        patch_World.paths["save"] = Path.Combine(dir, "save.txt");
        SaveFile.Load();
        LoadImages(dir);

        Hub.puzzleLineRefs.Clear();
        foreach (string key in Hub.puzzleData.Keys)
        {
            Hub.puzzleLineRefs[key] = new Hub.PuzzleLineRef
            {
                fromMe = new List<Hub.PuzzleLine>(),
                toMe = new List<Hub.PuzzleLine>()
            };
        }
        var puzzleLines = new List<Hub.PuzzleLine>();
        foreach (string[] tokens in GetTokens(dir, "puzzle_lines"))
        {
            var puzzleLine = new Hub.PuzzleLine
            {
                from = tokens[0],
                to = tokens[1],
                immediate = Hub.ParseBool(tokens[2])
            };
            puzzleLines.Add(puzzleLine);
            Hub.puzzleLineRefs[puzzleLine.from].fromMe.Add(puzzleLine);
            Hub.puzzleLineRefs[puzzleLine.to].toMe.Add(puzzleLine);
        }
        Hub.puzzleLines = puzzleLines.ToArray();

        var walking = new List<string>();
        foreach (var entry in Hub.puzzleData)
        {
            if (entry.Value.thumbnail == null && entry.Key != "hub")
            {
                walking.Add(entry.Key);
            }
        }
        if (walking.Count != 0)
        {
            Hub.puzzleNamesForWalking = walking.ToArray();
            Screenshotting = true;
            // devShrinkScreenDown does several things, most importantly
            // not rendering the player
            Controls.devShrinkScreenDown = true;
            patch_World.walking = true;
            patch_PauseMenu.ResumePublic();
            StartWalkingLevels();
            return;
        }

        orig_Load(data);
        LastMinuteHubFixes();
    }
}