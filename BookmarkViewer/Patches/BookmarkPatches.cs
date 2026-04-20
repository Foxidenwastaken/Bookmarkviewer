using HarmonyLib;
using HMUI;
using IPA.Utilities;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BookmarkViewer.Patches
{
    public static class ColourExtension
    {
        public static Color SetAlpha(this Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }
    }

    internal class BookmarkPatches
    {
        private static float _minX;
        private static float _maxX;

        private static Graphic? _handleGraphic;
        private static readonly List<Graphic> _graphicsPool = new();
        private static TextMeshProUGUI? _currentBookmarkText;

        private static List<Bookmark> _bookmarks = new();
        private static Bookmark? _currentBookmark;

        private static Vector3 _bookmarkGraphicScale = Vector3.one;

        private class Bookmark
        {
            public string Name = "";
            public float TimeInSeconds;
            public Color Color;
            public Graphic? Graphic;
        }

        private static float FindClosestFloat(float target)
        {
            if (_bookmarks.Count < 1)
            {
                return 0f;
            }

            float closest = _bookmarks[0].TimeInSeconds;
            float minDifference = Math.Abs(target - closest);

            foreach (Bookmark bookmark in _bookmarks)
            {
                float difference = Math.Abs(target - bookmark.TimeInSeconds);
                if (difference < minDifference)
                {
                    minDifference = difference;
                    closest = bookmark.TimeInSeconds;
                }
            }

            return closest;
        }

        private static void GetCurrentBookmark(float value)
        {
            Bookmark? bookmark = _bookmarks.FindLast(b => b.TimeInSeconds <= value);

            if (_currentBookmark != null && _currentBookmark.Graphic != null)
            {
                _currentBookmark.Graphic.color = _currentBookmark.Color.SetAlpha(0.7f);
                _currentBookmark.Graphic.transform.localScale = _bookmarkGraphicScale;
            }

            _currentBookmark = bookmark;

            if (bookmark != null && bookmark.Graphic != null)
            {
                bookmark.Graphic.color = bookmark.Color.SetAlpha(0.9f);
                bookmark.Graphic.transform.localScale =
                    Vector3.Scale(_bookmarkGraphicScale, new Vector3(1f, 1.15f, 1f));
            }
        }

        private static void UpdateTextValue()
        {
            if (_currentBookmarkText != null)
            {
                _currentBookmarkText.text = _currentBookmark != null ? _currentBookmark.Name : "";
            }
        }

        [HarmonyPatch(typeof(PracticeViewController))]
        [HarmonyPatch("HandleSongStartSliderValueDidChange")]
        internal class PracticeViewControllerHandleSongStartSliderValueDidChangePatch
        {
            private static void Prefix(
                PracticeViewController __instance,
                RangeValuesTextSlider slider,
                ref float value,
                ref object ____beatmapLevel)
            {
                if (!Config.Instance.Enabled || ____beatmapLevel == null)
                {
                    return;
                }

                float songDuration = GetSongDuration(____beatmapLevel);
                float maxStartSongTime = Mathf.Max(songDuration - 1f, 0f);
                float currentSongTime = Mathf.Lerp(0f, maxStartSongTime, value);

                float bookmarkTime = Config.Instance.SnapToBookmark
                    ? FindClosestFloat(currentSongTime)
                    : currentSongTime;

                if (Config.Instance.SnapToBookmark &&
                    Math.Abs(currentSongTime - bookmarkTime) < songDuration / 100f)
                {
                    float normalizedBookmarkValue = maxStartSongTime > 0f
                        ? Mathf.InverseLerp(0f, maxStartSongTime, bookmarkTime)
                        : 0f;

                    value = normalizedBookmarkValue;
                    slider.value = normalizedBookmarkValue;
                    currentSongTime = bookmarkTime;
                }

                GetCurrentBookmark(currentSongTime);
                UpdateTextValue();
            }
        }

        [HarmonyPatch(typeof(PracticeViewController))]
        [HarmonyPatch("DidActivate")]
        internal class PracticeViewControllerActivatePatch
        {
            private static void Postfix(
                PracticeViewController __instance,
                bool firstActivation,
                bool addedToHierarchy,
                bool screenSystemEnabling,
                ref object ____beatmapLevel)
            {
                if (!addedToHierarchy)
                {
                    return;
                }

                for (int i = 0; i < _graphicsPool.Count; i++)
                {
                    Graphic? graphic = _graphicsPool[i];
                    if (graphic == null)
                    {
                        _graphicsPool.Clear();
                        break;
                    }

                    graphic.gameObject.SetActive(false);
                }

                if (!Config.Instance.Enabled || ____beatmapLevel == null)
                {
                    _bookmarks.Clear();

                    if (_currentBookmarkText != null)
                    {
                        _currentBookmarkText.text = "";
                    }

                    return;
                }

                TimeSlider slider = __instance.GetField<TimeSlider, PracticeViewController>("_songStartSlider");
                Graphic sliderGraphic = slider.GetField<Graphic, TextSlider>("_handleGraphic");

                if (_handleGraphic == null)
                {
                    _handleGraphic = UnityEngine.Object.Instantiate(sliderGraphic);
                    UnityEngine.Object.DontDestroyOnLoad(_handleGraphic.gameObject);
                }

                _handleGraphic.transform.rotation = Quaternion.Euler(
                    0f,
                    0f,
                    Config.Instance.UnskewBookmarks ? 5f : 0f);

                SetupNameText(slider);
                GetSliderMinMax(slider, sliderGraphic);

                ObtainBookmarksFromLevel(__instance, ____beatmapLevel);
                UnityEngine.Debug.Log("[BookmarkViewer] Bookmarks found: " + _bookmarks.Count);

                SetBookmarkVisuals(sliderGraphic.transform, ____beatmapLevel);

                float songDuration = GetSongDuration(____beatmapLevel);
                float maxStartSongTime = Mathf.Max(songDuration - 1f, 0f);
                float currentSongTime = Mathf.Lerp(0f, maxStartSongTime, slider.value);

                GetCurrentBookmark(currentSongTime - songDuration / 100f);
                UpdateTextValue();
            }

            private static void SetBookmarkVisuals(Transform sliderGraphicTransform, object level)
            {
                int item = 0;

                foreach (Bookmark bookmarkItem in _bookmarks)
                {
                    Graphic bookmark;

                    if (_graphicsPool.Count > item)
                    {
                        bookmark = _graphicsPool[item];
                        bookmark.gameObject.SetActive(true);
                    }
                    else
                    {
                        if (_handleGraphic == null)
                        {
                            return;
                        }

                        bookmark = UnityEngine.Object.Instantiate(_handleGraphic, sliderGraphicTransform.parent);
                        _graphicsPool.Add(bookmark);
                    }

                    bookmarkItem.Graphic = bookmark;
                    bookmark.transform.localScale = _bookmarkGraphicScale;
                    bookmark.transform.position = new Vector3(
                        GetBookmarkXPosition(bookmarkItem.TimeInSeconds, level),
                        sliderGraphicTransform.position.y,
                        sliderGraphicTransform.position.z);
                    bookmark.color = bookmarkItem.Color.SetAlpha(0.75f);
                    item++;
                }
            }

            private static void SetupNameText(TimeSlider slider)
            {
                if (_currentBookmarkText != null)
                {
                    return;
                }

                Transform? labelTransform = slider.transform.parent?.Find("SongStartLabel");
                if (labelTransform == null)
                {
                    return;
                }

                GameObject currentBookmarkTextGo =
                    UnityEngine.Object.Instantiate(labelTransform.gameObject, slider.transform.parent);
                _currentBookmarkText = currentBookmarkTextGo.GetComponent<TextMeshProUGUI>();

                if (_currentBookmarkText == null)
                {
                    return;
                }

                _currentBookmarkText.transform.position = new Vector3(-0.025f, 2.04f, 4.35f);
                _currentBookmarkText.alignment = TextAlignmentOptions.Right;
                _currentBookmarkText.text = "";
            }

            private static void GetSliderMinMax(TimeSlider slider, Graphic sliderGraphic)
            {
                float value = slider.value;

                slider.value = slider.maxValue;
                _maxX = sliderGraphic.transform.position.x +
                    (Math.Abs(1f - Config.Instance.BookmarkWidthSize) * 0.05f);

                slider.value = slider.minValue;
                _minX = sliderGraphic.transform.position.x;

                slider.value = value;
                _bookmarkGraphicScale = Vector3.Scale(
                    sliderGraphic.transform.localScale,
                    new Vector3(Config.Instance.BookmarkWidthSize, 1f, 1f));
            }

            private static float GetBookmarkXPosition(float time, object level)
            {
                float songDuration = GetSongDuration(level);
                return Mathf.Lerp(_minX, _maxX, Mathf.InverseLerp(0f, songDuration, time));
            }
        }

        private static void ObtainBookmarksFromLevel(
            PracticeViewController viewController,
            object level)
        {
            _bookmarks.Clear();

            string? levelId =
                GetMemberValue(level, "levelID") as string ??
                GetMemberValue(level, "levelId") as string;

            if (string.IsNullOrEmpty(levelId))
            {
                UnityEngine.Debug.Log("[BookmarkViewer] No levelId found.");
                return;
            }

            if (!levelId.StartsWith("custom_level_", StringComparison.OrdinalIgnoreCase))
            {
                UnityEngine.Debug.Log("[BookmarkViewer] Not a custom level: " + levelId);
                return;
            }

            object? beatmapCharacteristic =
                GetMemberValue(viewController, "_beatmapCharacteristic") ??
                GetMemberValue(viewController, "beatmapCharacteristic") ??
                GetMemberValue(viewController, "_selectedBeatmapCharacteristic") ??
                GetMemberValue(viewController, "_currentBeatmapCharacteristic");

            string characteristicName = "Standard";

            if (beatmapCharacteristic != null)
            {
                characteristicName =
                    GetMemberValue(beatmapCharacteristic, "serializedName") as string ??
                    GetMemberValue(beatmapCharacteristic, "_serializedName") as string ??
                    "Standard";
            }

            object? beatmapKey =
                GetMemberValue(viewController, "_beatmapKey");

            object? selectedDifficultyObj =
                GetMemberValue(beatmapKey, "difficulty") ??
                GetMemberValue(beatmapKey, "_difficulty");

            string? difficultyName = ConvertDifficultyObjectToName(selectedDifficultyObj);

            string? difficultyLabel =
                GetMemberValue(beatmapKey, "difficultyLabel") as string ??
                GetMemberValue(beatmapKey, "_difficultyLabel") as string ??
                GetMemberValue(beatmapKey, "customDifficultyLabel") as string ??
                GetMemberValue(beatmapKey, "_customDifficultyLabel") as string ??
                GetMemberValue(beatmapKey, "beatmapLabel") as string ??
                GetMemberValue(beatmapKey, "_beatmapLabel") as string;

            UnityEngine.Debug.Log(
                $"[BookmarkViewer] characteristic={characteristicName}, difficulty={difficultyName}, label={difficultyLabel}");

            if (string.IsNullOrEmpty(difficultyName))
            {
                UnityEngine.Debug.Log("[BookmarkViewer] Missing difficulty.");
                return;
            }

            string? levelFolder = FindCustomLevelFolder(levelId, level);
            if (string.IsNullOrEmpty(levelFolder))
            {
                UnityEngine.Debug.Log("[BookmarkViewer] Could not locate custom level folder for " + levelId);
                return;
            }

            string? infoPath = FindInfoDat(levelFolder);
            if (string.IsNullOrEmpty(infoPath))
            {
                UnityEngine.Debug.Log("[BookmarkViewer] No Info.dat found in " + levelFolder);
                return;
            }

            string? beatmapFileName =
                FindBeatmapFileName(infoPath, characteristicName, difficultyName, difficultyLabel);

            if (string.IsNullOrEmpty(beatmapFileName))
            {
                UnityEngine.Debug.Log(
                    $"[BookmarkViewer] Could not find beatmap file in Info.dat for {characteristicName}/{difficultyName}/{difficultyLabel}");
                return;
            }

            string beatmapPath = Path.Combine(levelFolder, beatmapFileName);

            if (!File.Exists(beatmapPath))
            {
                UnityEngine.Debug.Log("[BookmarkViewer] Beatmap file missing: " + beatmapPath);
                return;
            }

            float bpm = ReadBpmFromInfoDat(infoPath);
            if (bpm <= 0f)
            {
                bpm = GetBeatsPerMinute(level);
            }

            LoadBookmarksFromBeatmapJson(beatmapPath, bpm);

            UnityEngine.Debug.Log($"[BookmarkViewer] Loaded {_bookmarks.Count} bookmarks from {beatmapPath}");
        }

        private static IEnumerable<string> GetCustomLevelRoots()
        {
            string gameRoot = Environment.CurrentDirectory;

            yield return Path.Combine(gameRoot, "Beat Saber_Data", "CustomLevels");
            yield return Path.Combine(gameRoot, "Beat Saber_Data", "CustomWIPLevels");
        }

        private static string ExtractHashFromLevelId(string levelId)
        {
            const string prefix = "custom_level_";
            string trimmed = levelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? levelId.Substring(prefix.Length)
                : levelId;

            int spaceIndex = trimmed.IndexOf(' ');
            return spaceIndex >= 0 ? trimmed.Substring(0, spaceIndex) : trimmed;
        }

        private static string? FindCustomLevelFolder(string levelId, object level)
        {
            string hash = ExtractHashFromLevelId(levelId);
            string? songName = GetSongName(level);

            foreach (string root in GetCustomLevelRoots())
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (string dir in Directory.EnumerateDirectories(root))
                {
                    string folderName = Path.GetFileName(dir);

                    if (!string.IsNullOrEmpty(hash) &&
                        folderName.IndexOf(hash, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return dir;
                    }
                }
            }

            if (!string.IsNullOrEmpty(songName))
            {
                foreach (string root in GetCustomLevelRoots())
                {
                    if (!Directory.Exists(root))
                    {
                        continue;
                    }

                    foreach (string dir in Directory.EnumerateDirectories(root))
                    {
                        string? infoPath = FindInfoDat(dir);
                        if (infoPath == null)
                        {
                            continue;
                        }

                        try
                        {
                            JObject info = JObject.Parse(File.ReadAllText(infoPath));
                            string? infoSongName = info["_songName"]?.Value<string>();

                            if (!string.IsNullOrEmpty(infoSongName) &&
                                string.Equals(infoSongName, songName, StringComparison.OrdinalIgnoreCase))
                            {
                                return dir;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }

            return null;
        }

        private static string? FindInfoDat(string levelFolder)
        {
            string infoUpper = Path.Combine(levelFolder, "Info.dat");
            if (File.Exists(infoUpper))
            {
                return infoUpper;
            }

            string infoLower = Path.Combine(levelFolder, "info.dat");
            if (File.Exists(infoLower))
            {
                return infoLower;
            }

            return null;
        }

        private static float ReadBpmFromInfoDat(string infoPath)
        {
            try
            {
                JObject info = JObject.Parse(File.ReadAllText(infoPath));
                return info["_beatsPerMinute"]?.Value<float>() ?? 0f;
            }
            catch
            {
                return 0f;
            }
        }

        private static string? FindBeatmapFileName(
            string infoPath,
            string characteristicName,
            string difficultyName,
            string? difficultyLabel)
        {
            try
            {
                JObject info = JObject.Parse(File.ReadAllText(infoPath));
                JArray? sets = info["_difficultyBeatmapSets"] as JArray;

                if (sets == null)
                {
                    return null;
                }

                List<JToken> candidates = new();

                foreach (JToken set in sets)
                {
                    string? setCharacteristic = set["_beatmapCharacteristicName"]?.Value<string>();
                    if (!string.Equals(setCharacteristic, characteristicName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    JArray? beatmaps = set["_difficultyBeatmaps"] as JArray;
                    if (beatmaps == null)
                    {
                        continue;
                    }

                    foreach (JToken beatmap in beatmaps)
                    {
                        string? diff = beatmap["_difficulty"]?.Value<string>();
                        if (!string.Equals(diff, difficultyName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        candidates.Add(beatmap);
                    }
                }

                if (candidates.Count == 0)
                {
                    return null;
                }

                if (!string.IsNullOrEmpty(difficultyLabel))
                {
                    foreach (JToken beatmap in candidates)
                    {
                        string? label =
                            beatmap["_customData"]?["_difficultyLabel"]?.Value<string>() ??
                            beatmap["_customData"]?["difficultyLabel"]?.Value<string>() ??
                            beatmap["_difficultyLabel"]?.Value<string>();

                        if (string.Equals(label, difficultyLabel, StringComparison.OrdinalIgnoreCase))
                        {
                            return beatmap["_beatmapFilename"]?.Value<string>();
                        }
                    }
                }

                if (candidates.Count == 1)
                {
                    return candidates[0]["_beatmapFilename"]?.Value<string>();
                }

                foreach (JToken beatmap in candidates)
                {
                    string? label =
                        beatmap["_customData"]?["_difficultyLabel"]?.Value<string>() ??
                        beatmap["_customData"]?["difficultyLabel"]?.Value<string>() ??
                        beatmap["_difficultyLabel"]?.Value<string>();

                    if (!string.IsNullOrEmpty(label))
                    {
                        return beatmap["_beatmapFilename"]?.Value<string>();
                    }
                }

                return candidates[0]["_beatmapFilename"]?.Value<string>();
            }
            catch
            {
                return null;
            }
        }

        private static void LoadBookmarksFromBeatmapJson(string beatmapPath, float bpm)
        {
            _bookmarks.Clear();

            JObject beatmap = JObject.Parse(File.ReadAllText(beatmapPath));

            JToken? bookmarksToken = beatmap["customData"]?["bookmarks"];
            bookmarksToken ??= beatmap["_customData"]?["_bookmarks"];
            bookmarksToken ??= beatmap["customData"]?["_bookmarks"];
            bookmarksToken ??= beatmap["_customData"]?["bookmarks"];

            if (bookmarksToken is not JArray bookmarksArray)
            {
                return;
            }

            foreach (JToken token in bookmarksArray)
            {
                float beat =
                    token["b"]?.Value<float>() ??
                    token["_time"]?.Value<float>() ??
                    0f;

                string name =
                    token["n"]?.Value<string>() ??
                    token["_name"]?.Value<string>() ??
                    "";

                JArray? color =
                    token["c"] as JArray ??
                    token["_color"] as JArray;

                Bookmark bookmark = new Bookmark
                {
                    TimeInSeconds = BeatsToSeconds(bpm, beat),
                    Name = name,
                    Color = Color.red
                };

                if (color != null && color.Count >= 3)
                {
                    bookmark.Color = new Color(
                        color[0]!.Value<float>(),
                        color[1]!.Value<float>(),
                        color[2]!.Value<float>(),
                        color.Count >= 4 ? color[3]!.Value<float>() : 1f);
                }

                _bookmarks.Add(bookmark);
            }

            _bookmarks = _bookmarks.OrderBy(b => b.TimeInSeconds).ToList();
        }

        private static string? GetSongName(object level)
        {
            return GetMemberValue(level, "songName") as string
                ?? GetMemberValue(level, "_songName") as string;
        }

        private static string? ConvertDifficultyObjectToName(object? diff)
        {
            if (diff == null)
            {
                return null;
            }

            if (diff is string s)
            {
                return s;
            }

            if (diff is int i)
            {
                return i switch
                {
                    0 => "Easy",
                    1 => "Normal",
                    2 => "Hard",
                    3 => "Expert",
                    4 => "ExpertPlus",
                    _ => null
                };
            }

            string text = diff.ToString() ?? "";
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            if (int.TryParse(text, out int parsed))
            {
                return parsed switch
                {
                    0 => "Easy",
                    1 => "Normal",
                    2 => "Hard",
                    3 => "Expert",
                    4 => "ExpertPlus",
                    _ => null
                };
            }

            return text;
        }

        private static float BeatsToSeconds(float bpm, float beat)
        {
            return bpm <= 0f ? 0f : (60f / bpm) * beat;
        }

        private static float GetSongDuration(object level)
        {
            object? value = GetMemberValue(level, "songDuration") ?? GetMemberValue(level, "_songDuration");
            return value is float f ? f : Convert.ToSingle(value ?? 0f);
        }

        private static float GetBeatsPerMinute(object level)
        {
            object? value = GetMemberValue(level, "beatsPerMinute") ?? GetMemberValue(level, "_beatsPerMinute");
            return value is float f ? f : Convert.ToSingle(value ?? 0f);
        }

        private static object? GetMemberValue(object? obj, string name)
        {
            if (obj == null)
            {
                return null;
            }

            Type type = obj.GetType();

            PropertyInfo? property = type.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (property != null)
            {
                return property.GetValue(obj);
            }

            FieldInfo? field = type.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (field != null)
            {
                return field.GetValue(obj);
            }

            return null;
        }
    }
}
