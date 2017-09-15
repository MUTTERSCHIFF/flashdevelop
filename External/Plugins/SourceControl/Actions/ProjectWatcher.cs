﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using PluginCore;
using PluginCore.Controls;
using PluginCore.Helpers;
using PluginCore.Localization;
using PluginCore.Managers;
using ProjectManager.Projects;
using SourceControl.Managers;
using SourceControl.Sources;
using SourceControl.Dialogs;

namespace SourceControl.Actions
{
    public static class ProjectWatcher
    {
        private static bool initialized = false;

        static internal readonly List<IVCManager> VCManagers = new List<IVCManager>();
        static VCManager vcManager;
        static FSWatchers fsWatchers;
        static OverlayManager ovManager;
        static Project currentProject;
        static List<string> addBuffer = new List<string>();

        public static bool Initialized { get { return initialized; } }
        public static Image Skin { get; set; }
        public static Project CurrentProject { get { return currentProject; } }
        public static VCManager VCManager { get { return vcManager; } }

        public static void Init()
        {
            if (initialized)
                return;

            if (Skin == null)
            {
                try
                {
                    Assembly assembly = Assembly.GetExecutingAssembly();
                    Skin = new Bitmap(assembly.GetManifestResourceStream(ScaleHelper.GetScale() > 1.5 ? "SourceControl.Resources.icons32.png" : "SourceControl.Resources.icons.png"));
                }
                catch
                {
                    Skin = ScaleHelper.GetScale() > 1.5 ? new Bitmap(320, 32) : new Bitmap(160, 16);
                }
            }
            
            fsWatchers = new FSWatchers();
            ovManager = new OverlayManager(fsWatchers);
            vcManager = new VCManager(ovManager);

            SetProject(PluginBase.CurrentProject as Project);

            initialized = true;
        }

        internal static void Dispose()
        {
            if (vcManager != null)
            {
                vcManager.Dispose();
                fsWatchers.Dispose();
                ovManager.Dispose();
                currentProject = null;
            }
        }

        internal static void SetProject(Project project)
        {
            currentProject = project;

            fsWatchers.SetProject(project);
            ovManager.Reset();

            foreach (ITabbedDocument document in PluginBase.MainForm.Documents)
                if (document.IsEditable) HandleFileReload(document.FileName);
        }

        internal static void SelectionChanged()
        {
            ovManager.SelectionChanged();
        }

        internal static void ForceRefresh()
        {
            fsWatchers.ForceRefresh();
        }


        #region file actions

        internal static bool HandleFileBeforeRename(string path)
        {
            WatcherVCResult result = fsWatchers.ResolveVC(path, true);
            if (result == null || result.Status == VCItemStatus.Unknown)
                return false;

            return result.Manager.FileActions.FileBeforeRename(path);
        }

        internal static bool HandleFileRename(string[] paths)
        {
            WatcherVCResult result = fsWatchers.ResolveVC(paths[0], true);
            if (result == null || result.Status == VCItemStatus.Unknown)
                return false;

            return result.Manager.FileActions.FileRename(paths[0], paths[1]);
        }

        internal static bool HandleFileDelete(string[] paths, bool confirm)
        {
            if (paths == null || paths.Length == 0) return false;
            WatcherVCResult result = fsWatchers.ResolveVC(Path.GetDirectoryName(paths[0]));
            if (result == null) return false;

            List<string> svnRemove = new List<string>();
            List<string> regularRemove = new List<string>();
            List<string> hasModification = new List<string>();
            List<string> hasUnknown = new List<string>();
            try
            {
                foreach (string path in paths)
                {
                    result = fsWatchers.ResolveVC(path, true);
                    if (result == null || result.Status == VCItemStatus.Unknown || result.Status == VCItemStatus.Ignored)
                    {
                        regularRemove.Add(path);
                    }
                    else
                    {
                        IVCManager manager = result.Manager;
                        string root = result.Watcher.Path;
                        int p = root.Length + 1;

                        if (Directory.Exists(path))
                        {
                            List<string> files = new List<string>();
                            GetAllFiles(path, files);
                            foreach (string file in files)
                            {
                                VCItemStatus status = manager.GetOverlay(file, root);
                                if (status == VCItemStatus.Unknown || status == VCItemStatus.Ignored)
                                    hasUnknown.Add(file.Substring(p));
                                else if (status > VCItemStatus.UpToDate)
                                    hasModification.Add(file.Substring(p));
                            }
                        }
                        else if (result.Status > VCItemStatus.UpToDate)
                            hasModification.Add(path);

                        if (svnRemove.Count > 0)
                        {
                            if (Path.GetDirectoryName(svnRemove[0]) != Path.GetDirectoryName(path))
                                throw new UnsafeOperationException(TextHelper.GetString("SourceControl.Info.ElementsLocatedInDiffDirs"));
                        }
                        svnRemove.Add(path);
                    }
                }
                if (regularRemove.Count > 0 && svnRemove.Count > 0)
                    throw new UnsafeOperationException(TextHelper.GetString("SourceControl.Info.MixedSelectionOfElements"));

                if (svnRemove.Count == 0 && regularRemove.Count > 0)
                    return false; // regular deletion
            }
            catch (UnsafeOperationException upex)
            {
                MessageBox.Show(upex.Message, TextHelper.GetString("SourceControl.Info.UnsafeDeleteOperation"), MessageBoxButtons.OK, MessageBoxIcon.Stop);
                return true; // prevent regular deletion
            }

            if (hasUnknown.Count > 0 && confirm) //this never happens (at least on git), because it is always handled by the "regular deletion" part above
            {
                string title = TextHelper.GetString("FlashDevelop.Title.ConfirmDialog");
                string msg = TextHelper.GetString("SourceControl.Info.ConfirmUnversionedDelete") + "\n\n" + GetSomeFiles(hasUnknown);
                if (MessageBox.Show(msg, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return true;
            }

            if (hasModification.Count > 0 && confirm)
            {
                string title = TextHelper.GetString("FlashDevelop.Title.ConfirmDialog");
                string msg = TextHelper.GetString("SourceControl.Info.ConfirmLocalModsDelete") + "\n\n" + GetSomeFiles(hasModification);
                if (MessageBox.Show(msg, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return true;
            }

            if ((hasModification.Count > 0 || svnRemove.Count > 0) && confirm) //there are versioned files
            {
                switch (PluginMain.SCSettings.ShouldDelete)
                {
                    case RememberValue.Yes:
                        //TODO: there should probably still be a message wether you really want to delete the files.
                        return result.Manager.FileActions.FileDelete(paths, confirm);
                    case RememberValue.Ask:
                        using (var dialog = new Dialogs.SourceControlDialog(TextHelper.GetString("FlashDevelop.Title.ConfirmDialog"),
                            "Would you like to remove the file(s) from version control?"))
                        {
                            dialog.ShowDialog();
                            if (dialog.Remember)
                            {
                                PluginMain.SCSettings.ShouldDelete = dialog.DialogResult == DialogResult.Yes ? RememberValue.Yes : RememberValue.No;
                            }
                            if (dialog.DialogResult == DialogResult.Yes)
                            {
                                return result.Manager.FileActions.FileDelete(paths, confirm);
                            }
                        }
                        break;
                }
            }

            return false;
        }

        private static string GetSomeFiles(List<string> list)
        {
            if (list.Count < 10) return String.Join("\n", list.ToArray());
            return String.Join("\n", list.GetRange(0, 9).ToArray()) + "\n(...)\n" + list[list.Count - 1];
        }

        private static void GetAllFiles(string path, List<string> files)
        {
            string[] search = Directory.GetFiles(path);
            foreach (string file in search)
            {
                string name = Path.GetFileName(file);
                if (name[0] == '.') continue;
                files.Add(file);
            }

            string[] dirs = Directory.GetDirectories(path);
            foreach (string dir in dirs)
            {
                string name = Path.GetFileName(dir);
                if (name[0] == '.') continue;
                FileInfo info = new FileInfo(dir);
                if ((info.Attributes & FileAttributes.Hidden) > 0) continue;
                GetAllFiles(dir, files);
            }
        }

        internal static bool HandleFileMove(string[] paths)
        {
            WatcherVCResult result = fsWatchers.ResolveVC(paths[0], true);
            if (result == null || result.Status == VCItemStatus.Unknown)
                return false; // origin not under VC, ignore
            WatcherVCResult result2 = fsWatchers.ResolveVC(paths[1], true);
            if (result2 == null || result2.Status == VCItemStatus.Unknown)
                return false; // target dir not under VC, ignore

            return result.Manager.FileActions.FileMove(paths[0], paths[1]);
        }

        /// <summary>
        /// Called after a file was sucessfully moved.
        /// </summary>
        /// <param name="file">The file that was moved</param>
        internal static void HandleFileMoved(string fromFile, string toFile)
        {
            WatcherVCResult result = fsWatchers.ResolveVC(toFile, true);
            if (result == null || result.Status == VCItemStatus.Unknown)
                return; // target dir not under VC, ignore

            if (PluginBase.CurrentProject != null)
            {
                fromFile = PluginBase.CurrentProject.GetRelativePath(fromFile);
                toFile = PluginBase.CurrentProject.GetRelativePath(toFile);
            }
            
            var message = AskForCommit($"Moved {fromFile} to {toFile}");

            if (message != null)
                result.Manager.Commit(new[] { toFile }, message);
        }

        internal static bool HandleBuildProject()
        {
            WatcherVCResult result = fsWatchers.ResolveVC(currentProject.OutputPathAbsolute, true);
            if (result == null || result.Status == VCItemStatus.Unknown)
                return false;

            return result.Manager.FileActions.BuildProject();
        }

        internal static bool HandleTestProject()
        {
            WatcherVCResult result = fsWatchers.ResolveVC(currentProject.OutputPathAbsolute, true);
            if (result == null || result.Status == VCItemStatus.Unknown)
                return false;

            return result.Manager.FileActions.TestProject();
        }

        internal static bool HandleSaveProject(string fileName)
        {
            WatcherVCResult result = fsWatchers.ResolveVC(fileName, true);
            if (result == null || result.Status == VCItemStatus.Unknown)
                return false;

            return result.Manager.FileActions.SaveProject();
        }

        internal static bool HandleFileNew(string path)
        {
            if (!initialized)
                return false;

            addBuffer.Add(path); //at this point there is not yet an ITabbedDocument for the file

            WatcherVCResult result = fsWatchers.ResolveVC(path, true);
            if (result == null || result.Status == VCItemStatus.Unknown)
                return false;

            return false;
        }

        internal static bool HandleFileOpen(string path)
        {
            if (!initialized)
                return false;

            WatcherVCResult result = fsWatchers.ResolveVC(path, true);
            if (result == null)
                return false;

            if (addBuffer.Remove(path))
            {
                MessageBar.ShowQuestion("Would you like to add this file to version control?", new[] { "Yes", "No" },
                    s =>
                    {
                        if (s == "Yes")
                        {
                            result.Manager.FileActions.FileNew(path);
                            ForceRefresh();
                        }

                        TraceManager.Add(s);
                    });
                //TODO: Add some way to remember this
            }

            
            if (result.Status == VCItemStatus.Unknown)
                return false;

            return result.Manager.FileActions.FileOpen(path);
        }

        internal static bool HandleFileReload(string path)
        {
            if (!initialized)
                return false;

            WatcherVCResult result = fsWatchers.ResolveVC(path, true);
            if (result == null || result.Status == VCItemStatus.Unknown)
                return false;

            return result.Manager.FileActions.FileReload(path);
        }

        internal static bool HandleFileModifyRO(string path)
        {
            if (!initialized)
                return false;

            WatcherVCResult result = fsWatchers.ResolveVC(path, true);
            if (result == null || result.Status == VCItemStatus.Unknown)
                return false;

            return result.Manager.FileActions.FileModifyRO(path);
        }

        #endregion

        static string AskForCommit(string message)
        {
            var title = TextHelper.GetString("FlashDevelop.Title.ConfirmDialog");
            var msg = "Would you like to create a commit for this action?";

            //TODO: Add "Never button / checkbox"

            using (LineEntryDialog led = new LineEntryDialog(title, msg, message))
            {
                var result = led.ShowDialog();
                if (result == DialogResult.Cancel) //Never
                {
                    //TODO: save this
                    return null;
                }
                if (result != DialogResult.Yes || led.Line == "")
                    return null;

                return led.Line;
            }
        }

    }
    
    class UnsafeOperationException:Exception
    {
        public UnsafeOperationException(string message)
            : base(message)
        {
        }
    }

}
