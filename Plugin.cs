using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore.LowLevel;

namespace SSLChineseFontRuntime
{
    [BepInPlugin("codex.shellshocklive.zhcn.font.runtime", "ShellShock Live ZH-CN Runtime Font", "1.0.5")]
    public sealed class Plugin : BasePlugin
    {
        private const string BundledFontName = "微软雅黑.ttf";
        private const int TmpSamplingPointSize = 32;
        private const int TmpAtlasPadding = 4;
        private const int MaxExtraFallbackAssets = 32;
        private static TMP_FontAsset FallbackAsset;
        private static string RuntimeFontPath;
        private static Font LegacyFont;
        private static ManualLogSource Logger;
        private static readonly HashSet<char> BakedCharacters = new HashSet<char>();
        private static readonly HashSet<char> MissingGlyphWarnings = new HashSet<char>();
        private static readonly List<TMP_FontAsset> ExtraFallbackAssets = new List<TMP_FontAsset>();
        private static int _applyFailureCount;
        private static int _bakeFailureCount;
        private Harmony _harmony;

        public override void Load()
        {
            try
            {
                Logger = Log;
                string fontPath = FindBundledFontPath();
                RuntimeFontPath = fontPath;
                LegacyFont = CreateLegacyFont(fontPath);
                FallbackAsset = CreateFallbackFontAsset(fontPath);
                AddGlobalFallback(FallbackAsset);
                PrebakeChineseCorpus();

                _harmony = new Harmony("codex.shellshocklive.zhcn.font.runtime");
                int patches = PatchTmpTextTouchPoints();
                StartFontSceneSweeper();
                Log.LogInfo("Loaded runtime Chinese font fallback: font=" + fontPath + ", patches=" + patches);
            }
            catch (Exception ex)
            {
                Log.LogError("Runtime Chinese font fallback failed: " + ex);
            }
        }

        private static void StartFontSceneSweeper()
        {
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<FontSceneSweeper>();
                FontSceneSweeper.Logger = Logger;
                FontSceneSweeper.InitializeStatic();
                GameObject host = new GameObject("SSLChineseFontRuntime_FontSceneSweeper");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent(Il2CppType.Of<FontSceneSweeper>());
            }
            catch (Exception ex)
            {
                Logger?.LogWarning("Failed to start bounded Chinese font scene sweeper: " + ex.Message);
            }
        }

        private static string FindBundledFontPath()
        {
            string pluginDir = Path.Combine(Paths.PluginPath, "SSLChineseFontRuntime");
            string pluginFont = Path.Combine(pluginDir, BundledFontName);
            if (File.Exists(pluginFont))
                return pluginFont;

            string configFont = Path.Combine(Paths.ConfigPath, BundledFontName);
            if (File.Exists(configFont))
                return configFont;

            throw new FileNotFoundException("Bundled Chinese font not found.", pluginFont);
        }

        private static Font CreateLegacyFont(string fontPath)
        {
            Font font = new Font();
            Font.Internal_CreateFontFromPath(font, fontPath);
            font.name = "Microsoft YaHei Runtime Legacy";
            return font;
        }

        private static TMP_FontAsset CreateFallbackFontAsset(string fontPath)
        {
            Font font = CreateLegacyFont(fontPath);

            TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(
                font,
                TmpSamplingPointSize,
                TmpAtlasPadding,
                GlyphRenderMode.SDFAA,
                4096,
                4096,
                AtlasPopulationMode.Dynamic);

            asset.name = "Microsoft YaHei Runtime SDF";
            asset.atlasPopulationMode = AtlasPopulationMode.Dynamic;
            asset.isMultiAtlasTexturesEnabled = true;
            return asset;
        }

        private static void PrebakeChineseCorpus()
        {
            string corpus = LoadChineseCorpus();
            if (string.IsNullOrEmpty(corpus))
                return;

            EnsureTmpCharacters(corpus);
            Logger?.LogInfo("Pre-baked Microsoft YaHei Chinese corpus: chars=" + BakedCharacters.Count);
        }

        private static string LoadChineseCorpus()
        {
            StringBuilder builder = new StringBuilder();
            AppendChineseCharacters(builder, "中文汉化设置游戏武器生命光环爆裂弹跳微软雅黑哨官霜枫念朗询卿千舰御阴轰宝渗透愚蠢耀钻镖蕉姜荐偷");
            AppendChineseCharacters(builder, "，。！？：；、“”‘’（）【】《》〈〉…—·+-*/%0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ ");

            TryAppendCorpusFile(builder, Path.Combine(Paths.ConfigPath, "ssl_weapon_name_zhcn.csv"));
            TryAppendCorpusFile(builder, Path.Combine(Paths.PluginPath, "SSLWeaponNameTranslator", "ssl_weapon_name_zhcn.csv"));

            return builder.ToString();
        }

        private static void TryAppendCorpusFile(StringBuilder builder, string path)
        {
            try
            {
                if (!File.Exists(path))
                    return;
                AppendChineseCharacters(builder, File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                Logger?.LogWarning("Failed to load Chinese corpus file " + path + ": " + ex.Message);
            }
        }

        private static void AppendChineseCharacters(StringBuilder builder, string value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            HashSet<char> seen = new HashSet<char>(builder.ToString().ToCharArray());
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                if (!IsChineseFontCharacter(ch) || seen.Contains(ch))
                    continue;
                seen.Add(ch);
                builder.Append(ch);
            }
        }

        private static void AddGlobalFallback(TMP_FontAsset fallback)
        {
            Il2CppSystem.Collections.Generic.List<TMP_FontAsset> globalFallbacks = TMP_Settings.fallbackFontAssets;
            if (globalFallbacks != null && !globalFallbacks.Contains(fallback))
                globalFallbacks.Add(fallback);

            TMP_FontAsset defaultFont = TMP_Settings.defaultFontAsset;
            AddFontFallback(defaultFont, fallback);
        }

        private static void AddFontFallback(TMP_FontAsset fontAsset, TMP_FontAsset fallback)
        {
            if (fontAsset == null || fallback == null || fontAsset == fallback)
                return;

            Il2CppSystem.Collections.Generic.List<TMP_FontAsset> table = fontAsset.fallbackFontAssetTable;
            if (table == null)
            {
                table = new Il2CppSystem.Collections.Generic.List<TMP_FontAsset>();
                fontAsset.fallbackFontAssetTable = table;
            }

            if (!table.Contains(fallback))
                table.Add(fallback);
        }

        private int PatchTmpTextTouchPoints()
        {
            Type type = typeof(TMP_Text);
            if (type == null)
            {
                Log.LogWarning("TMP_Text type not found.");
                return 0;
            }

            int count = 0;
            count += TryPatch(AccessTools.PropertySetter(type, "font"), nameof(FontSetterPostfix));
            count += TryPatch(AccessTools.PropertySetter(type, "text"), nameof(TmpTextSetterPostfix));

            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type))
            {
                if (method.Name != "SetText")
                    continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length >= 1 && parameters[0].ParameterType == typeof(string) && parameters[0].Name == "sourceText")
                    count += TryPatch(method, nameof(TmpSetTextPostfix));
            }

            Type uiText = typeof(UnityEngine.UI.Text);
            count += TryPatch(AccessTools.PropertySetter(uiText, "text"), nameof(UiTextTouchPostfix));

            Type tmpInputField = typeof(TMP_InputField);
            count += TryPatch(AccessTools.PropertySetter(tmpInputField, "text"), nameof(TmpInputFieldTextPostfix));

            Type uiInputField = typeof(UnityEngine.UI.InputField);
            count += TryPatch(AccessTools.PropertySetter(uiInputField, "text"), nameof(UiInputFieldTextPostfix));

            return count;
        }

        private int TryPatch(MethodInfo method, string postfixName)
        {
            if (method == null)
                return 0;

            try
            {
                _harmony.Patch(method, postfix: new HarmonyMethod(typeof(Plugin), postfixName));
                return 1;
            }
            catch (Exception ex)
            {
                Log.LogWarning("Failed to patch " + method.Name + ": " + ex.Message);
                return 0;
            }
        }

        private static void FontSetterPostfix(TMP_FontAsset value)
        {
            if (FallbackAsset == null)
                return;
            AddGlobalFallback(FallbackAsset);
            AddFontFallback(value, FallbackAsset);
            FontSceneSweeper.TrySweepFromTick();
        }

        private static void TmpTextSetterPostfix(TMP_Text __instance, string value)
        {
            if (FallbackAsset == null)
                return;

            AddGlobalFallback(FallbackAsset);
            FontSceneSweeper.TrySweepFromTick();
            if (ContainsCjk(value))
            {
                EnsureTmpCharacters(value);
                ApplyYaHeiTmpFont(__instance, value);
            }
        }

        private static void TmpSetTextPostfix(TMP_Text __instance, string sourceText)
        {
            if (FallbackAsset == null)
                return;

            AddGlobalFallback(FallbackAsset);
            FontSceneSweeper.TrySweepFromTick();
            if (ContainsCjk(sourceText))
            {
                EnsureTmpCharacters(sourceText);
                ApplyYaHeiTmpFont(__instance, sourceText);
            }
        }

        private static void EnsureTmpCharacters(string value)
        {
            if (FallbackAsset == null || string.IsNullOrEmpty(value))
                return;
            try
            {
                string filtered = FilterUnbakedChineseFontCharacters(value);
                if (string.IsNullOrEmpty(filtered))
                    return;

                string missing;
                FallbackAsset.TryAddCharacters(filtered, out missing);
                if (!string.IsNullOrEmpty(missing))
                    missing = RetryMissingCharactersOneByOne(missing);
                if (!string.IsNullOrEmpty(missing))
                    missing = TryAddCharactersToExtraFallbackAssets(missing);

                HashSet<char> missingChars = new HashSet<char>();
                if (!string.IsNullOrEmpty(missing))
                {
                    for (int i = 0; i < missing.Length; i++)
                        missingChars.Add(missing[i]);
                }

                for (int i = 0; i < filtered.Length; i++)
                {
                    if (!missingChars.Contains(filtered[i]))
                        BakedCharacters.Add(filtered[i]);
                }

                if (!string.IsNullOrEmpty(missing) && _bakeFailureCount < 8 && HasNewMissingGlyphWarning(missing))
                {
                    _bakeFailureCount++;
                    LogMissingGlyphDetails(missing);
                }
            }
            catch (Exception ex)
            {
                if (_bakeFailureCount < 8)
                {
                    _bakeFailureCount++;
                    Logger?.LogWarning("Failed to bake Microsoft YaHei glyphs: " + ex.Message);
                }
            }
        }

        private static string RetryMissingCharactersOneByOne(string missing)
        {
            if (FallbackAsset == null || string.IsNullOrEmpty(missing))
                return string.Empty;

            StringBuilder stillMissing = new StringBuilder();
            HashSet<char> attempted = new HashSet<char>();
            for (int i = 0; i < missing.Length; i++)
            {
                char ch = missing[i];
                if (!attempted.Add(ch))
                    continue;

                try
                {
                    string oneMissing;
                    FallbackAsset.TryAddCharacters(ch.ToString(), out oneMissing);
                    if (string.IsNullOrEmpty(oneMissing))
                        BakedCharacters.Add(ch);
                    else
                        stillMissing.Append(ch);
                }
                catch
                {
                    stillMissing.Append(ch);
                }
            }
            return stillMissing.ToString();
        }

        private static string TryAddCharactersToExtraFallbackAssets(string missing)
        {
            if (string.IsNullOrEmpty(missing))
                return string.Empty;

            StringBuilder stillMissing = new StringBuilder();
            HashSet<char> attempted = new HashSet<char>();
            for (int i = 0; i < missing.Length; i++)
            {
                char ch = missing[i];
                if (!attempted.Add(ch))
                    continue;

                if (TryAddCharacterToAnyExtraFallback(ch))
                    BakedCharacters.Add(ch);
                else
                    stillMissing.Append(ch);
            }
            return stillMissing.ToString();
        }

        private static bool TryAddCharacterToAnyExtraFallback(char ch)
        {
            for (int i = 0; i < ExtraFallbackAssets.Count; i++)
            {
                if (TryAddCharacterToAsset(ExtraFallbackAssets[i], ch))
                    return true;
            }

            while (ExtraFallbackAssets.Count < MaxExtraFallbackAssets)
            {
                TMP_FontAsset asset = CreateExtraFallbackAsset();
                if (asset == null)
                    return false;
                ExtraFallbackAssets.Add(asset);
                AddGlobalFallback(asset);
                AddFontFallback(FallbackAsset, asset);
                for (int i = 0; i < ExtraFallbackAssets.Count; i++)
                    AddFontFallback(ExtraFallbackAssets[i], asset);

                if (TryAddCharacterToAsset(asset, ch))
                {
                    Logger?.LogInfo("Created Microsoft YaHei extra TMP fallback atlas " + ExtraFallbackAssets.Count + "/" + MaxExtraFallbackAssets);
                    return true;
                }
            }

            return false;
        }

        private static TMP_FontAsset CreateExtraFallbackAsset()
        {
            try
            {
                if (string.IsNullOrEmpty(RuntimeFontPath))
                    return null;
                TMP_FontAsset asset = CreateFallbackFontAsset(RuntimeFontPath);
                asset.name = "Microsoft YaHei Runtime SDF Extra " + (ExtraFallbackAssets.Count + 1);
                return asset;
            }
            catch (Exception ex)
            {
                Logger?.LogWarning("Failed to create Microsoft YaHei extra TMP fallback atlas: " + ex.Message);
                return null;
            }
        }

        private static bool TryAddCharacterToAsset(TMP_FontAsset asset, char ch)
        {
            if (asset == null)
                return false;
            try
            {
                string missing;
                asset.TryAddCharacters(ch.ToString(), out missing);
                return string.IsNullOrEmpty(missing);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasNewMissingGlyphWarning(string missing)
        {
            bool hasNew = false;
            for (int i = 0; i < missing.Length; i++)
            {
                if (MissingGlyphWarnings.Add(missing[i]))
                    hasNew = true;
            }
            return hasNew;
        }

        private static void LogMissingGlyphDetails(string missing)
        {
            if (string.IsNullOrEmpty(missing))
                return;

            StringBuilder builder = new StringBuilder();
            int limit = Math.Min(missing.Length, 16);
            for (int i = 0; i < limit; i++)
            {
                if (i > 0)
                    builder.Append(' ');
                builder.Append("U+");
                builder.Append(((int)missing[i]).ToString("X4"));
                builder.Append('(');
                builder.Append(missing[i]);
                builder.Append(')');
            }
            Logger?.LogWarning("Microsoft YaHei missing glyphs after bake: count=" + missing.Length + ", chars=" + builder);
        }

        private static string FilterUnbakedChineseFontCharacters(string value)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                if (!IsChineseFontCharacter(ch) || BakedCharacters.Contains(ch))
                    continue;
                builder.Append(ch);
            }
            return builder.ToString();
        }

        private static void ApplyYaHeiTmpFont(TMP_Text text, string value)
        {
            if (text == null || FallbackAsset == null || !ContainsCjk(value))
                return;

            try
            {
                text.font = FallbackAsset;
                text.fontSharedMaterial = FallbackAsset.material;
                text.SetAllDirty();
            }
            catch (Exception ex)
            {
                if (_applyFailureCount < 8)
                {
                    _applyFailureCount++;
                    Logger?.LogWarning("Failed to apply Microsoft YaHei TMP font: " + ex.Message);
                }
            }
        }

        private static void UiTextTouchPostfix(UnityEngine.UI.Text __instance, string value)
        {
            if (__instance == null || LegacyFont == null)
                return;
            FontSceneSweeper.TrySweepFromTick();
            if (ContainsCjk(value))
                ApplyLegacyFont(__instance);
        }

        private static void TmpInputFieldTextPostfix(TMP_InputField __instance, string value)
        {
            if (__instance == null || FallbackAsset == null || !ContainsCjk(value))
                return;

            AddGlobalFallback(FallbackAsset);
            FontSceneSweeper.TrySweepFromTick();
            EnsureTmpCharacters(value);
            ApplyYaHeiTmpFont(__instance.textComponent, value);
        }

        private static void UiInputFieldTextPostfix(UnityEngine.UI.InputField __instance, string value)
        {
            if (__instance == null || LegacyFont == null || !ContainsCjk(value))
                return;

            FontSceneSweeper.TrySweepFromTick();
            ApplyLegacyFont(__instance.textComponent);
        }

        private static void ApplyLegacyFont(UnityEngine.UI.Text text)
        {
            text.font = LegacyFont;
        }

        private static bool ContainsCjk(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;
            for (int i = 0; i < value.Length; i++)
            {
                if (IsCjk(value[i]))
                    return true;
            }
            return false;
        }

        private static bool IsChineseFontCharacter(char ch)
        {
            return IsCjk(ch)
                || (ch >= '\u3000' && ch <= '\u303f')
                || (ch >= '\uff00' && ch <= '\uffef')
                || ch == '\u00b7'
                || ch == '\u2014'
                || ch == '\u2026';
        }

        private static bool IsCjk(char ch)
        {
            return (ch >= '\u3400' && ch <= '\u9fff') || (ch >= '\uf900' && ch <= '\ufaff');
        }

        public sealed class FontSceneSweeper : MonoBehaviour
        {
            public static ManualLogSource Logger;
            public static int MaxSweeps = 5;

            private static bool _initialized;
            private static float _nextSweepTime;
            private static int _sweepCount;
            private static string _lastSceneName = string.Empty;

            public FontSceneSweeper(IntPtr ptr) : base(ptr)
            {
            }

            public static void InitializeStatic()
            {
                _initialized = true;
                _lastSceneName = string.Empty;
                ResetSweepBudgetForScene();
            }

            public void Update()
            {
                TrySweepFromTick();
            }

            public static void TrySweepFromTick()
            {
                ResetSweepBudgetForScene();
                if (!_initialized || _sweepCount >= MaxSweeps)
                    return;
                if (Time.realtimeSinceStartup < _nextSweepTime)
                    return;

                _sweepCount++;
                _nextSweepTime = Time.realtimeSinceStartup + 3.0f;
                ScanExistingChineseText();
            }

            private static void ResetSweepBudgetForScene()
            {
                try
                {
                    string sceneName = SceneManager.GetActiveScene().name;
                    if (sceneName == null)
                        sceneName = string.Empty;
                    if (sceneName == _lastSceneName)
                        return;

                    _lastSceneName = sceneName;
                    _sweepCount = 0;
                    _nextSweepTime = Time.realtimeSinceStartup + 0.75f;
                    Logger?.LogInfo("Chinese font scene sweep budget reset for scene: " + sceneName);
                }
                catch
                {
                    if (_nextSweepTime <= 0f)
                        _nextSweepTime = Time.realtimeSinceStartup + 1.0f;
                }
            }

            private static void ScanExistingChineseText()
            {
                int tmpApplied = 0;
                int uiApplied = 0;

                try
                {
                    Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UnityEngine.Object> tmpObjects =
                        UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<TMP_Text>(), true);
                    foreach (UnityEngine.Object raw in tmpObjects)
                    {
                        TMP_Text text = raw == null ? null : raw.TryCast<TMP_Text>();
                        if (text == null || !ContainsCjk(text.text))
                            continue;
                        EnsureTmpCharacters(text.text);
                        ApplyYaHeiTmpFont(text, text.text);
                        tmpApplied++;
                    }
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning("Chinese TMP scene sweep failed: " + ex.Message);
                }

                try
                {
                    Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UnityEngine.Object> uiObjects =
                        UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<UnityEngine.UI.Text>(), true);
                    foreach (UnityEngine.Object raw in uiObjects)
                    {
                        UnityEngine.UI.Text text = raw == null ? null : raw.TryCast<UnityEngine.UI.Text>();
                        if (text == null || !ContainsCjk(text.text))
                            continue;
                        ApplyLegacyFont(text);
                        uiApplied++;
                    }
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning("Chinese UI.Text scene sweep failed: " + ex.Message);
                }

                if (_sweepCount <= 4 || _sweepCount == MaxSweeps)
                    Logger?.LogInfo("Chinese font scene sweep " + _sweepCount + "/" + MaxSweeps + ": tmp=" + tmpApplied + ", ui=" + uiApplied);
            }
        }

    }
}
