﻿using System.IO;
using System.Linq;
using Uplift.Common;
using Uplift.Packages;
using Uplift.Schemas;
using Uplift.SourceControl;
using Uplift.DependencyResolution;
using System.Text.RegularExpressions;
using UnityEditor;
using System;
using System.Collections.Generic;

namespace Uplift
{
    class UpliftManager
    {
        // --- SINGLETON DECLARATION ---
        protected static UpliftManager instance;

        internal UpliftManager() {

        }

        public static UpliftManager Instance()
        {
            if (instance == null)
            {
                InitializeInstance();
            }

            return instance;
        }

        public static void ResetInstances()
        {
            instance = null;
            Upfile.ResetInstance();
            Upbring.ResetInstance();
            InitializeInstance();
        }

        internal static void InitializeInstance()
        {
            instance = new UpliftManager();
            instance.upfile = Upfile.Instance();
        }

        // --- CLASS DECLARATION ---
        public static readonly string lockfilePath = "Upfile.lock";
        protected Upfile upfile;

        public enum InstallStrategy {
            ONLY_LOCKFILE,
            INCOMPLETE_LOCKFILE,
            UPDATE_LOCKFILE
        }

        public void InstallDependencies(InstallStrategy strategy = InstallStrategy.UPDATE_LOCKFILE, bool refresh = false)
        {
            if (refresh) UpliftManager.ResetInstances();
            PackageRepo[] targets = GetTargets(GetDependencySolver(), strategy);
            InstallPackages(targets);
        }

        private PackageRepo[] GetTargets(IDependencySolver solver, InstallStrategy strategy)
        {
            DependencyDefinition[] upfileDependencies = upfile.Dependencies;
            PackageRepo[] installableDependencies = IdentifyInstallable(solver.SolveDependencies(upfileDependencies));
            PackageRepo[] targets = new PackageRepo[0];
            bool present = File.Exists(lockfilePath);

            if(strategy == InstallStrategy.UPDATE_LOCKFILE || (strategy == InstallStrategy.INCOMPLETE_LOCKFILE && !present))
            {
                GenerateLockfile(installableDependencies);
                targets = installableDependencies;
            }
            else if(strategy == InstallStrategy.INCOMPLETE_LOCKFILE)
            {
                // Case where the file does not exist is already covered
                targets = LoadLockfile();

                if(installableDependencies.Length != targets.Length)
                {
                    // Lockfile needs to be updated
                    if(installableDependencies.Length > targets.Length)
                    {
                        List<DependencyDefinition> toSolve = new List<DependencyDefinition>();
                        foreach(PackageRepo pr in targets)
                        {
                            toSolve.Add(new DependencyDefinition() {
                                Name = pr.Package.PackageName,
                                Version = pr.Package.PackageVersion + "!" // Require exact version not to modify existing lockfile
                            });
                        }

                        foreach(DependencyDefinition def in upfileDependencies)
                        {
                            if(toSolve.Any(d => d.Name == def.Name)) continue;
                            toSolve.Add(def);
                        }

                        targets = IdentifyInstallable(solver.SolveDependencies(toSolve.ToArray()));
                        GenerateLockfile(targets);
                    }
                    else
                    {
                        List<PackageRepo> targetList = targets.ToList();
                        // Some lockfile dependencies are no longer used
                        foreach(PackageRepo pr in targets)
                            if(!installableDependencies.Any(dep => dep.Package.PackageName == pr.Package.PackageName))
                                targetList.Remove(pr);

                        targets = targetList.ToArray();
                    }
                }
            }
            else if(strategy == InstallStrategy.ONLY_LOCKFILE)
            {
                if(!present)
                    throw new ApplicationException("Uplift cannot install dependencies in strategy ONLY_LOCKFILE if there is no lockfile");
                targets = LoadLockfile();
            }
            else
            {
                throw new ArgumentException("Unknown install strategy: " + strategy);
            }

            return targets;
        }

        private PackageRepo[] IdentifyInstallable(DependencyDefinition[] definitions)
        {
            PackageRepo[] result = new PackageRepo[definitions.Length];
            for(int i = 0; i < definitions.Length; i++)
                result[i] = PackageList.Instance().FindPackageAndRepository(definitions[i]);

            return result;
        }

        public IDependencySolver GetDependencySolver()
        {
            TransitiveDependencySolver dependencySolver = new TransitiveDependencySolver();
            dependencySolver.CheckConflict += SolveVersionConflict;

            return dependencySolver;
        }

        private void SolveVersionConflict(ref DependencyNode existing, DependencyNode compared)
        {
            IVersionRequirement restricted;
            try
            {
                restricted = existing.Requirement.RestrictTo(compared.Requirement);
            }
            catch (IncompatibleRequirementException e)
            {
                UnityEngine.Debug.LogError("Unsolvable version conflict in the dependency graph");
                throw new IncompatibleRequirementException("Some dependencies " + existing.Name + " are not compatible.\n", e);
            }

            existing.Requirement = restricted;
        }

        private void GenerateLockfile(PackageRepo[] solvedDependencies)
        {
            string result = "# DEPENDENCIES\n";
            foreach(PackageRepo pr in solvedDependencies)
            {
                //PackageRepo pr = PackageList.Instance().FindPackageAndRepository(def);
                result += pr.Package.PackageName + " (" + pr.Package.PackageVersion + ")\n";
            }

            using (System.IO.StreamWriter file = new System.IO.StreamWriter(lockfilePath, false))
            {
                file.WriteLine(result);
            }
        }

        private PackageRepo[] LoadLockfile()
        {
            string pattern = @"([\w\.]+)\s\(([\w\.]+)\)";
            string[] lines = File.ReadAllLines(lockfilePath);
            PackageRepo[] result = new PackageRepo[lines.Length - 2];
            
            DependencyDefinition temp;
            for(int i = 1; i < lines.Length - 1; i++)
            {
                Match match = Regex.Match(lines[i], pattern);
                if(!match.Success)
                {
                    UnityEngine.Debug.LogErrorFormat("Could not load line \"{0}\" in Upfile.lock", lines[i]);
                    continue;
                }
                
                temp = new DependencyDefinition() {
                    Name = match.Groups[1].Value,
                    Version = match.Groups[2].Value + "!"
                };
                result[i - 1] = PackageList.Instance().FindPackageAndRepository(temp);
            }

            return result;
        }

        public void InstallPackages(PackageRepo[] targets)
        {
            using(LogAggregator LA = LogAggregator.InUnity(
                "Installed {0} dependencies successfully",
                "Installed {0} dependencies successfully but warnings were raised",
                "Some errors occured while installing {0} dependencies",
                targets.Length
                ))
            {
                // Remove installed dependencies that are no longer in the dependency tree
                foreach (InstalledPackage ip in Upbring.Instance().InstalledPackage)
                {
                    if (targets.Any(tar => tar.Package.PackageName == ip.Name)) continue;

                    UnityEngine.Debug.Log("Removing unused dependency on " + ip.Name);
                    NukePackage(ip.Name);
                }

                foreach (PackageRepo pr in targets)
                {
                    if (pr.Repository != null)
                    {
                        if (Upbring.Instance().InstalledPackage.Any(ip => ip.Name == pr.Package.PackageName))
                        {
                            UpdatePackage(pr);
                        }
                        else
                        {
                            DependencyDefinition def = upfile.Dependencies.Any(d => d.Name == pr.Package.PackageName) ?
                                upfile.Dependencies.First(d => d.Name == pr.Package.PackageName) :
                                new DependencyDefinition() { Name = pr.Package.PackageName, Version = pr.Package.PackageVersion };

                            using (TemporaryDirectory td = pr.Repository.DownloadPackage(pr.Package))
                            {
                                InstallPackage(pr.Package, td, def);
                            }
                        }
                    }
                    else
                    {
                        UnityEngine.Debug.LogError("No repository for package " + pr.Package.PackageName);
                    }
                }
            }

            UnityHacks.BuildSettingsEnforcer.EnforceAssetSave();
        }

        public void NukeAllPackages()
        {
            Upbring upbring = Upbring.Instance();
            using (LogAggregator LA = LogAggregator.InUnity(
                "{0} packages were successfully nuked",
                "{0} packages were successfully nuked but warnings were raised",
                "Some errors occured while nuking {0} packages",
                upbring.InstalledPackage.Length
                ))
            {
                foreach (InstalledPackage package in upbring.InstalledPackage)
                {
                    package.Nuke();
                    upbring.RemovePackage(package);
                }

                //TODO: Remove file when Upbring properly removes everything
                Upbring.RemoveFile();
            }
        }

        public string GetPackageDirectory(Upset package)
        {
            return package.PackageName + "~" + package.PackageVersion;
        }

        public string GetRepositoryInstallPath(Upset package)
        {
            return Path.Combine(upfile.GetPackagesRootPath(), GetPackageDirectory(package));
        }

        //FIXME: This is super unsafe right now, as we can copy down into the FS.
        // This should be contained using kinds of destinations.
        private void InstallPackage(Upset package, TemporaryDirectory td, DependencyDefinition dependencyDefinition)
        {
            GitIgnorer VCSHandler = new GitIgnorer();

            using (LogAggregator LA = LogAggregator.InUnity(
                "Package {0} was successfully installed",
                "Package {0} was successfully installed but raised warnings",
                "An error occured while installing package {0}",
                package.PackageName
                ))
            {
                Upbring upbring = Upbring.Instance();
                
                // Note: Full package is ALWAYS copied to the upackages directory right now
                string localPackagePath = GetRepositoryInstallPath(package);
                upbring.AddPackage(package);
                if (!Directory.Exists(localPackagePath))
                    Directory.CreateDirectory(localPackagePath);

                FileSystemUtil.CopyDirectory(td.Path, localPackagePath);
                upbring.AddLocation(package, InstallSpecType.Root, localPackagePath);

                VCSHandler.HandleDirectory(upfile.GetPackagesRootPath());

                InstallSpecPath[] specArray;
                if (package.Configuration == null)
                {
                    // If there is no Configuration present we assume
                    // that the whole package is wrapped in "InstallSpecType.Base"
                    InstallSpecPath wrapSpec = new InstallSpecPath
                    {
                        Path = "",
                        Type = InstallSpecType.Base
                    };

                    specArray = new[] { wrapSpec };
                }
                else
                {
                    specArray = package.Configuration;
                }

                foreach (InstallSpecPath spec in specArray)
                {
                    if (dependencyDefinition.SkipInstall != null && dependencyDefinition.SkipInstall.Any(skip => skip.Type == spec.Type)) continue;

                    var sourcePath = Uplift.Common.FileSystemUtil.JoinPaths(td.Path, spec.Path);

                    PathConfiguration PH = upfile.GetDestinationFor(spec);
                    if (dependencyDefinition.OverrideDestination != null && dependencyDefinition.OverrideDestination.Any(over => over.Type == spec.Type))
                    {
                        PH.Location = Uplift.Common.FileSystemUtil.MakePathOSFriendly(dependencyDefinition.OverrideDestination.First(over => over.Type == spec.Type).Location);
                    }

                    var packageStructurePrefix =
                        PH.SkipPackageStructure ? "" : GetPackageDirectory(package);

                    var destination = Path.Combine(PH.Location, packageStructurePrefix);

                    // Working with single file
                    if (File.Exists(sourcePath))
                    {
                        // Working with singular file
                        if (!Directory.Exists(destination))
                        {
                            Directory.CreateDirectory(destination);
                            VCSHandler.HandleFile(destination);
                        }
                        if (Directory.Exists(destination))
                        { // we are copying a file into a directory
                            destination = System.IO.Path.Combine(destination, System.IO.Path.GetFileName(sourcePath));
                        }
                        File.Copy(sourcePath, destination);
                        Uplift.Common.FileSystemUtil.TryCopyMeta(sourcePath, destination);

                        if (destination.StartsWith("Assets"))
                        {
                            TryUpringAddGUID(upbring, sourcePath, package, spec.Type, destination);
                        }
                        else
                        {
                            upbring.AddLocation(package, spec.Type, destination);
                        }

                    }

                    // Working with directory
                    if (Directory.Exists(sourcePath))
                    {
                        // Working with directory
                        Uplift.Common.FileSystemUtil.CopyDirectoryWithMeta(sourcePath, destination);
                        if(!PH.SkipPackageStructure)
                            VCSHandler.HandleDirectory(destination);
                        
                        bool useGuid = destination.StartsWith("Assets");
                        foreach (var file in Uplift.Common.FileSystemUtil.RecursivelyListFiles(sourcePath, true))
                        {
                            if(useGuid)
                                TryUpringAddGUID(upbring, file, package, spec.Type, destination);
                            else
                                upbring.AddLocation(package, spec.Type, Path.Combine(destination, file));
                            
                            if(PH.SkipPackageStructure)
                                VCSHandler.HandleFile(Path.Combine(destination, file));
                        }
                    }
                }

                upbring.SaveFile();

                td.Dispose();
                UnityHacks.BuildSettingsEnforcer.EnforceAssetSave();
            }
        }

        private void CheckGUIDConflicts(string sourceDirectory, Upset package)
        {
            foreach(string file in FileSystemUtil.RecursivelyListFiles(sourceDirectory))
            {
                if (!file.EndsWith(".meta")) continue;
                string guid = LoadGUID(file);
                string guidPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(guidPath))
                {
                    if(File.Exists(guidPath) || Directory.Exists(guidPath))
                    {
                        // the guid is cached and the associated file/directory exists
                        Directory.Delete(sourceDirectory, true);
                        throw new ApplicationException(
                            string.Format(
                                "The guid {0} is already used and tracks {1}. Uplift was trying to import a file with meta at {2} for package {3}. Uplift cannot install this package, please clean your project before trying again.",
                                guid,
                                guidPath,
                                file,
                                package.PackageName
                                )
                            );
                    }
                    // else, the guid is cached but there are no longer anything linked with it
                }
            }
        }

        private void TryUpringAddGUID(Upbring upbring, string file, Upset package, InstallSpecType type, string destination)
        {
            if (file.EndsWith(".meta")) return;
            string metaPath = Path.Combine(destination, file + ".meta");
            if (!File.Exists(metaPath))
            {
                upbring.AddLocation(package, type, Path.Combine(destination, file));
                return;
            }
            string guid = LoadGUID(metaPath);
            upbring.AddGUID(package, type, guid);
        }

        private string LoadGUID(string path)
        {
            const string guidMatcherRegexp = @"guid: (?<guid>\w+)";
            using (StreamReader sr = new StreamReader(path))
            {
                string line;
                while((line = sr.ReadLine()) != null)
                {
                    Match matchObject = Regex.Match(line, guidMatcherRegexp);
                    if (matchObject.Success) return matchObject.Groups["guid"].ToString();
                }
            }

            throw new InvalidDataException(string.Format("File {0} does not contain guid information", path));
        }

        private void UpdatePackage(Upset package, TemporaryDirectory td)
        {
            NukePackage(package.PackageName);

            DependencyDefinition definition = Upfile.Instance().Dependencies.First(dep => dep.Name == package.PackageName);
            InstallPackage(package, td, definition);
        }

        public void UpdatePackage(PackageRepo newer, bool updateDependencies = true)
        {
            InstalledPackage installed = Upbring.Instance().InstalledPackage.First(ip => ip.Name == newer.Package.PackageName);
            
            // If latest version is greater than the one installed, update to it
            if (VersionParser.GreaterThan(newer.Package.PackageVersion, installed.Version))
            {
                using (TemporaryDirectory td = newer.Repository.DownloadPackage(newer.Package))
                {
                    UpdatePackage(newer.Package, td);
                }
            }
            else
            {
                UnityEngine.Debug.Log(string.Format("Latest version of {0} is already installed ({1})", installed.Name, installed.Version));
                return;
            }

            if (updateDependencies)
            {
                DependencyDefinition[] packageDependencies = PackageList.Instance().ListDependenciesRecursively(
                    GetDependencySolver()
                    .SolveDependencies(upfile.Dependencies)
                    .First(dep => dep.Name == newer.Package.PackageName)
                    );
                foreach(DependencyDefinition def in packageDependencies)
                {
                    PackageRepo dependencyPR = PackageList.Instance().FindPackageAndRepository(def);
                    if (Upbring.Instance().InstalledPackage.Any(ip => ip.Name == def.Name))
                    {
                        UpdatePackage(dependencyPR, false);
                    }
                    else
                    {
                        using (TemporaryDirectory td = dependencyPR.Repository.DownloadPackage(dependencyPR.Package))
                        {
                            InstallPackage(dependencyPR.Package, td, def);
                        }
                    }
                }
            }
        }

        // What's the difference between Nuke and Uninstall?
        // Nuke doesn't care for dependencies (if present)
        public void NukePackage(string packageName)
        {
            Upbring upbring = Upbring.Instance();
            InstalledPackage package = upbring.GetInstalledPackage(packageName);
            package.Nuke();
            upbring.RemovePackage(package);
            upbring.SaveFile();
            UnityHacks.BuildSettingsEnforcer.EnforceAssetSave();
        }
    }
}
