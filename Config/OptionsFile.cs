using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace ActuallyWorkingEmployees.Config
{
    /// <summary>
    /// Keeps the AWE categories in <c>UserData/ActuallyWorkingEmployees.cfg</c> instead of the shared
    /// <c>MelonPreferences.cfg</c>.
    ///
    /// MelonLoader writes the shared file for every mod at once. When one save of it throws (another mod's key that
    /// Tomlet cannot write is enough), MelonLoader flags the file (Preferences.IO.File.WasError) and skips every later
    /// save of it for the rest of the session, so no AWE change reached the disk. A file of its own is saved on its own
    /// and is not affected by the shared one.
    ///
    /// On the first start the values MelonLoader already read from the shared file are written into the new file, so
    /// settings saved by an older version carry over. Changes are written about a second after the last one, so a
    /// crash or a forced quit keeps them.
    /// </summary>
    internal static class OptionsFile
    {
        internal const string FileName = "ActuallyWorkingEmployees.cfg";

        /// <summary>Seconds after the last change before the file is written; a dragged slider writes once.</summary>
        private const float SaveDelay = 1f;

        private static readonly List<MelonPreferences_Category> _categories = new List<MelonPreferences_Category>();
        private static float _saveAt = -1f;
        private static int _saves;

        internal static string FilePath { get; private set; }

        /// <summary>Number of writes this session, for the dev console and the tests.</summary>
        internal static int Saves => _saves;

        /// <summary>Moves <paramref name="categories"/> into the AWE file and loads it, or creates it from the current values.</summary>
        internal static void Attach(IEnumerable<MelonPreferences_Category> categories)
        {
            FilePath = Path.Combine(MelonEnvironment.UserDataDirectory, FileName);
            bool existed = File.Exists(FilePath);
            foreach (MelonPreferences_Category cat in categories)
            {
                cat.SetFilePath(FilePath, autoload: false, printmsg: false);
                _categories.Add(cat);
            }
            if (_categories.Count == 0)
            {
                Core.Log?.Warning("[Options] no option category to store in " + FileName + ".");
                return;
            }

            if (existed)
            {
                // One load refreshes every category that points at this file.
                _categories[0].LoadFromFile(false);
                Core.Log?.Msg("[Options] settings read from " + FilePath + ".");
            }
            else
            {
                Save("first start, values taken over from MelonPreferences.cfg");
            }
        }

        /// <summary>Schedules a write; called after a local value changed.</summary>
        internal static void MarkDirty()
        {
            if (_categories.Count == 0) return;
            _saveAt = Time.unscaledTime + SaveDelay;
        }

        internal static void Tick()
        {
            if (_saveAt < 0f || Time.unscaledTime < _saveAt) return;
            _saveAt = -1f;
            Save("changed");
        }

        /// <summary>Writes a pending change now, e.g. when the game quits.</summary>
        internal static void Flush()
        {
            if (_saveAt < 0f) return;
            _saveAt = -1f;
            Save("pending change at quit");
        }

        private static void Save(string reason)
        {
            try
            {
                DateTime before = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
                // SaveToFile writes the whole file each time; each category first adds its entries to the document.
                for (int i = 0; i < _categories.Count; i++) _categories[i].SaveToFile(false);
                // MelonLoader catches its own write errors and then skips the file silently; the timestamp tells.
                bool written = File.Exists(FilePath) && File.GetLastWriteTimeUtc(FilePath) != before;
                if (!written)
                {
                    Core.Log?.Error("[Options] " + FilePath + " was not written (" + reason + "). See the MelonLoader error above; settings changed now are lost at quit.");
                    return;
                }
                _saves++;
                Core.Log?.Msg("[Options] settings saved to " + FileName + " (" + reason + ").");
            }
            catch (Exception e)
            {
                Core.Log?.Error("[Options] saving " + FilePath + " failed (" + reason + "): " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
