using DSoft.VersionChanger.Extensions;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Flavor;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace DSoft.VersionChanger.Data
{
    /// <summary>
    /// Processes the solution
    /// </summary>
    public class SolutionProcessor : IDisposable
    {
        #region Fields
        private List<FailedProject> _failedProjects;
        private Solution _mainSolution;
        #endregion

        #region Properties

        public event EventHandler<int> OnLoadedProjects = delegate { };

        public event EventHandler<Tuple<int, string>> OnStartingProject = delegate { };

        public bool DetectedUnloadedProjects { get; set; }

        public List<FailedProject> FailedProjects
        {
            get
            {
                if (_failedProjects == null)
                    _failedProjects = new List<FailedProject>();

                return _failedProjects;
            }
            set { _failedProjects = value; }
        }

        #endregion

        #region Constructors

        public SolutionProcessor(Solution MainSolution)
        {
            _mainSolution = MainSolution;
        }


        #endregion

        #region Public Methods


        /// <summary>
        /// Builds the versions from the projects
        /// </summary>
        /// <param name="MainSolution">The main solution.</param>
        /// <returns></returns>
        /// <exception cref="System.Exception"></exception>
        public ProjectVersionCollection BuildVersions(Solution MainSolution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var versions = new ProjectVersionCollection();

            var projs = FindProjects(MainSolution);

            OnLoadedProjects(this, projs.Count);

           

            try
            {
                var loopPosition = 0;

                foreach (Project proj in projs)
                {
                    loopPosition++;

                    OnStartingProject.Invoke(this, new Tuple<int, string>(loopPosition, proj.Name));

                    if (!string.IsNullOrEmpty(proj.FileName) 
                        && proj.ProjectItems != null)
                    {
                        
                        bool hasCocoa = false;
                        bool hasAndroid = false;
                        bool isSdk = false;
                        bool hasUwp = false;

                        var projectTypeGuids = GetProjectTypeGuids(proj);
                        ProjectItem projectItem = null;

                        if (projectTypeGuids != null)
                        {
                            var ignorableTypes = new List<string>()
                            {
                                "{54435603-DBB4-11D2-8724-00A0C9A8B90C}"
                                , "{930C7802-8A8C-48F9-8165-68863BCCD9DD}"
                                , "{7CF6DF6D-3B04-46F8-A40B-537D21BCA0B4}" // Sandcast Help File Builder project 
                            };

                            var firstTypeId = projectTypeGuids.First().ToUpper();

                            //if the type of the project is on the ignore list then skip
                            if (ignorableTypes.Contains(firstTypeId))
                            {
                                continue;
                            }

                            var iOSTypes = new List<string> { "{FEACFBD2-3405-455C-9665-78FE426C6842}", "{EE2C853D-36AF-4FDB-B1AD-8E90477E2198}" };
                            var androidTypes = new List<string> { "{EFBA0AD7-5A72-4C68-AF49-83D382785DCF}", "{10368E6C-D01B-4462-8E8B-01FC667A7035}" };
                            var macTypes = new List<string> { "{A3F8F2AB-B479-4A4A-A458-A89E7DC349F1}", "{EE2C853D-36AF-4FDB-B1AD-8E90477E2198}" };
                            var uwpTypes = new List<string> { "{A5A43C5B-DE2A-4C0C-9213-0A381AF9435A}" };

                            if (iOSTypes.Contains(projectTypeGuids.First()) || macTypes.Contains(projectTypeGuids.First()))
                            {
                                hasCocoa = true;      
                            }
                            else if (androidTypes.Contains(projectTypeGuids.First()))
                            {
                                hasAndroid = true;
                            }
                            else if (uwpTypes.Contains(projectTypeGuids.First()))
                            {
                                hasUwp = true;
                            }

                            projectItem = FindAssemblyInfoProjectItem(proj.ProjectItems);

                            if (projectItem == null)
                            {
                                // Before failing, check whether the .csproj is actually SDK-style.
                                // This happens when a project was migrated to SDK format but the .sln
                                // still carries the legacy C# project type GUID {FAE04EC0-...}, causing
                                // GetProjectTypeGuids() to return non-null even though there is no
                                // AssemblyInfo.cs — version properties live in the .csproj itself.
                                if (IsSdkStyleProjectFile(proj.FileName))
                                {
                                    isSdk = true;
                                }
                                else
                                {
                                    var newFailedProject = new FailedProject()
                                    {
                                        Name = proj.Name,
                                        Reason = Enum.FailureReason.MissingAssemblyInfo,
                                    };

                                    FailedProjects.Add(newFailedProject);

                                    continue;
                                }
                            }

                        }
                        else
                        {
                            projectItem = FindAssemblyInfoProjectItem(proj.ProjectItems);

                            isSdk = true;
                        }

                        var newVersion = LoadVersionNumber(proj, projectItem, hasCocoa, hasAndroid, hasUwp, isSdk);

                        if (newVersion != null)
                        {
                            versions.Add(newVersion);

                            if (hasCocoa == true)
                                versions.HasIosMac = true;

                            if (hasAndroid == true)
                                versions.HasAndroid = true;
                        }

                    }

                }
            }
            catch (Exception)
            {
                throw;
            }

            return versions;
        }

        /// <summary>
        /// Update an SDK style csproj.
        /// All version tags are rewritten directly in the project XML — relying on
        /// <c>realProject.Properties</c> is unreliable for SDK-style projects because
        /// many version properties are calculated/read-only through DTE and silently
        /// discard assignments, leaving AssemblyVersion/FileVersion/Version untouched.
        /// </summary>
        public void UpdateSdkProject(Project realProject, AssemblyVersionOptions versionOptions, Version newVersion, Version fileVersion = null, string versionSuffix = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            //flush any pending in-memory edits so we don't fight VS when rewriting from disk
            realProject.Save();

            var effectiveFileVersion = fileVersion ?? newVersion;
            var assemblyVersionStr = versionOptions.GetVersionString(newVersion);
            var fileVersionStr = versionOptions.GetVersionString(effectiveFileVersion);
            var fullSemVer = versionOptions.CalculateVersion(newVersion, versionSuffix);
            var baseSemVer = versionOptions.CalculateVersion(newVersion);

            var lines = File.ReadAllLines(realProject.FileName);
            var changed = false;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                if (versionOptions.UpdateAssemblyVersion)
                    changed |= TryReplaceXmlTag(ref line, "AssemblyVersion", assemblyVersionStr);

                if (versionOptions.UpdateFileVersion)
                    changed |= TryReplaceXmlTag(ref line, "FileVersion", fileVersionStr);

                if (versionOptions.UpdateAssemblyVersionPrefix)
                    changed |= TryReplaceXmlTag(ref line, "VersionPrefix", assemblyVersionStr);

                if (versionOptions.UpdateVersion)
                    changed |= TryReplaceXmlTag(ref line, "Version", fullSemVer);

                if (versionOptions.UpdatePackageVersion)
                    changed |= TryReplaceXmlTag(ref line, "PackageVersion", fullSemVer);

                if (versionOptions.UpdateInformationalVersion)
                {
                    changed |= TryReplaceXmlTag(ref line, "InformationalVersion", fullSemVer);
                    changed |= TryReplaceXmlTag(ref line, "AssemblyInformationalVersion", fullSemVer);
                }

                if (!string.IsNullOrEmpty(versionSuffix))
                    changed |= TryReplaceXmlTag(ref line, "VersionSuffix", versionSuffix);

                if (versionOptions.UpdateAppDisplayVersion)
                    changed |= TryReplaceXmlTag(ref line, "ApplicationDisplayVersion", baseSemVer);

                if (versionOptions.UpdateAppVersion)
                    changed |= TryReplaceXmlTag(ref line, "ApplicationVersion", newVersion.Major.ToString());

                lines[i] = line;
            }

            if (changed)
                File.WriteAllLines(realProject.FileName, lines);
        }

        /// <summary>
        /// Replace the inner text of the first occurrence of <c>&lt;tag&gt;…&lt;/tag&gt;</c>
        /// on the given line. Supports optional attributes on the opening tag.
        /// Returns true when the line was actually modified.
        /// </summary>
        private static bool TryReplaceXmlTag(ref string line, string tag, string newValue)
        {
            //match <Tag> or <Tag attr="..."> … </Tag> on a single line
            var pattern = $@"<{Regex.Escape(tag)}(?<attrs>(\s[^>/]*)?)>(?<value>[^<]*)</{Regex.Escape(tag)}>";
            var match = Regex.Match(line, pattern);

            if (!match.Success)
                return false;

            if (match.Groups["value"].Value == newValue)
                return false;

            var attrs = match.Groups["attrs"].Value;
            var replacement = $"<{tag}{attrs}>{newValue}</{tag}>";
            line = line.Substring(0, match.Index) + replacement + line.Substring(match.Index + match.Length);
            return true;
        }

        /// <summary>
        /// Update an old framework style csproj
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="newAssemblyVersion">The new assembly version.</param>
        /// <param name="newFileVersion">The new file version.</param>
        public void UpdateFrameworkProject(ProjectItem item, AssemblyVersionOptions versionOptions, Version newAssemblyVersion, Version newFileVersion = null, string versionSuffix = null, bool assemblyFileInfo_AddSuffix = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!item.IsOpen)
                item.Open();

            var aDoc = item.Document;

            TextDocument editDoc = (TextDocument)aDoc.Object("TextDocument");
            var objEditPt = editDoc.StartPoint.CreateEditPoint();
            objEditPt.StartOfDocument();

            var endPpint = editDoc.EndPoint.CreateEditPoint();
            endPpint.EndOfDocument();

            string searchText = "AssemblyVersion";
            string searchText2 = "AssemblyFileVersion";
            string searchText3 = "AssemblyInformationalVersion";
            string searchVstart = "(\"";
            string assemblyText = "assembly:";

            //if the file version is null, as seperate version have not been set
            if (newFileVersion == null)
            {
                newFileVersion = newAssemblyVersion;
            }

            var endLine = endPpint.Line;

            var lastLine = false;

            while (true)
            {
                var aLine = objEditPt.GetText(objEditPt.LineLength);

                //Debug.WriteLine($"Line: {objEditPt.Line} - {aLine}");

                if (!aLine.StartsWith("//")
                        && !aLine.StartsWith("'"))
                {

                    if (aLine.ToLower().Contains(assemblyText))
                    {
                        if (aLine.Contains(searchText) && versionOptions.UpdateAssemblyVersion == true)
                        {
                            //now get the version number
                            int locationStart = aLine.IndexOf(searchText);
                            var searchLength = searchText.Length;

                            string initail = aLine.Substring((locationStart + searchText.Length));
                            var openerlocationStart = initail.IndexOf(searchVstart);

                            searchLength += (openerlocationStart + searchVstart.Length);
                            //locationStart += openerlocationStart;

                            string firstBit = aLine.Substring(0, (locationStart + searchLength));
                            string remaining = aLine.Substring((locationStart + searchLength));
                            int locationEnd = remaining.IndexOf("\"");
                            string end = remaining.Substring(locationEnd);

                            var newVersionValue = versionOptions.CalculateVersion(newAssemblyVersion);

                            var newLine = string.Format("{0}{1}{2}", firstBit, newVersionValue, end);

                            objEditPt.ReplaceText(objEditPt.LineLength, newLine, (int)vsEPReplaceTextOptions.vsEPReplaceTextKeepMarkers);

                        }

                        if (aLine.Contains(searchText2) && versionOptions.UpdateFileVersion == true)
                        {
                            int locationStart = aLine.IndexOf(searchText2);
                            var searchLength = searchText2.Length;

                            string initail = aLine.Substring((locationStart + searchText2.Length));
                            var openerlocationStart = initail.IndexOf(searchVstart);

                            searchLength += (openerlocationStart + searchVstart.Length);

                            string firstBit = aLine.Substring(0, (locationStart + searchLength));
                            string remaining = aLine.Substring((locationStart + searchLength));
                            int locationEnd = remaining.IndexOf("\"");
                            string end = remaining.Substring(locationEnd);

                            string newFileVersionValue;
                            if (assemblyFileInfo_AddSuffix == true)
                            {
                                newFileVersionValue = versionOptions.CalculateVersion(newFileVersion, versionSuffix);
                            }
                            else newFileVersionValue = versionOptions.CalculateVersion(newFileVersion);

                            var newLine = string.Format("{0}{1}{2}", firstBit, newFileVersionValue.ToString(), end);

                           objEditPt.ReplaceText(objEditPt.LineLength, newLine, (int)vsEPReplaceTextOptions.vsEPReplaceTextKeepMarkers);


                        }

                        if (aLine.Contains(searchText3) && versionOptions.UpdateInformationalVersion == true)
                        {
                            int locationStart = aLine.IndexOf(searchText3);
                            var searchLength = searchText3.Length;

                            string initail = aLine.Substring((locationStart + searchText3.Length));
                            var openerlocationStart = initail.IndexOf(searchVstart);

                            searchLength += (openerlocationStart + searchVstart.Length);

                            string firstBit = aLine.Substring(0, (locationStart + searchLength));
                            string remaining = aLine.Substring((locationStart + searchLength));
                            int locationEnd = remaining.IndexOf("\"");
                            string end = remaining.Substring(locationEnd);

                            var newFileVersionValue = versionOptions.CalculateVersion(newFileVersion, versionSuffix);

                            var newLine = string.Format("{0}{1}{2}", firstBit, newFileVersionValue.ToString(), end);


                            objEditPt.ReplaceText(objEditPt.LineLength, newLine, (int)vsEPReplaceTextOptions.vsEPReplaceTextKeepMarkers);

                            //var aLine2 = objEditPt.GetText(objEditPt.LineLength);

                        }
                    }

                }

                //check to see if the last line has already been processed
                if (objEditPt.Line.Equals(endLine) && lastLine == true)
                    break;//break the loop

                objEditPt.LineDown();
                objEditPt.StartOfLine();

                //if the we're on the last line, allow one further loop to process the line
                if (objEditPt.Line.Equals(endLine))
                    lastLine = true;

            }

            item.Save();
            aDoc.Close();
            
            
        }

        public void Dispose()
        {
            _mainSolution = null;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Finds the projects in the solution
        /// </summary>
        /// <param name="solution">The solution.</param>
        /// <returns></returns>
        private ArrayList FindProjects(Solution solution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            ArrayList projectst = new ArrayList();

            foreach (Project proj in solution.Projects)
            {

                if (string.Compare(EnvDTE.Constants.vsProjectKindUnmodeled, proj.Kind, System.StringComparison.OrdinalIgnoreCase) == 0)
                {
                    DetectedUnloadedProjects = true;

                    continue;
                }
                    

                if (proj.FullName == "")
                {
                    //folder
                    //int count = proj.ProjectItems.Count;

                    AddSubProjects(proj, projectst);
                }
                else
                {
                    projectst.Add(proj);
                }

            }


            return projectst;
        }

        /// <summary>
        /// Adds the sub projects.
        /// </summary>
        /// <param name="Proj">The proj.</param>
        /// <param name="Items">The items.</param>
        private void AddSubProjects(Project Proj, ArrayList Items)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            //MessageBox.Show(proj.FullName);
            Debug.WriteLine(Proj.FullName);

            if (Proj.FullName == "")
            {
                //folder
                //int count = Proj.ProjectItems.Count;

                foreach (ProjectItem proj2 in Proj.ProjectItems)
                {
                    if (proj2.SubProject != null)
                    {
                        AddSubProjects(proj2.SubProject, Items);
                    }
                    //MessageBox.Show(proj2.SubProject.ToString());
                }
            }
            else
            {
                Items.Add(Proj);
            }
        }

        /// <summary>
        /// Finds the assembly information project item.
        /// </summary>
        /// <param name="items">The items.</param>
        /// <returns></returns>
        private ProjectItem FindAssemblyInfoProjectItem(ProjectItems items)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            return FindProjectItem(items, "assemblyinfo");
        }

        private ProjectItem FindProjectItem(ProjectItems items, string fileName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            //if (items == null)
            //    return null;

            foreach (ProjectItem aItem in items)
            {
                

                if (aItem.ProjectItems?.Count > 0)
                {
                    var aResult = FindProjectItem(aItem.ProjectItems, fileName);

                    if (aResult != null)
                        return aResult;

                }
                else if (aItem.Name.ToLower().Contains(fileName))
                {
                    return aItem;
                }

            }


            return null;
        }

        private string[] GetProjectTypeGuids(EnvDTE.Project proj)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string projectTypeGuids = string.Empty;
            object service = null;
            Microsoft.VisualStudio.Shell.Interop.IVsSolution solution = null;
            Microsoft.VisualStudio.Shell.Interop.IVsHierarchy hierarchy = null;
            IVsAggregatableProjectCorrected aggregatableProject = null;
            int result = 0;

            service = GetService(proj.DTE, typeof(SVsSolution));
            solution = (Microsoft.VisualStudio.Shell.Interop.IVsSolution)service;

            result = solution.GetProjectOfUniqueName(proj.UniqueName, out hierarchy);

            if (result == 0)
            {
                aggregatableProject = hierarchy as IVsAggregatableProjectCorrected;

                if (aggregatableProject != null)
                {
                    result = aggregatableProject.GetAggregateProjectTypeGuids(out projectTypeGuids);
                }
                
            }

            if (String.IsNullOrWhiteSpace(projectTypeGuids))
                return null;

            return projectTypeGuids.Split(';');


        }

        private object GetService(object serviceProvider, Type type)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            return GetService(serviceProvider, type.GUID);
        }

        private object GetService(object serviceProviderObject, System.Guid guid)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            object service = null;
            Microsoft.VisualStudio.OLE.Interop.IServiceProvider serviceProvider = null;
            IntPtr serviceIntPtr;
            int hr = 0;
            Guid SIDGuid;
            Guid IIDGuid;

            SIDGuid = guid;
            IIDGuid = SIDGuid;
            serviceProvider = (Microsoft.VisualStudio.OLE.Interop.IServiceProvider)serviceProviderObject;
            hr = serviceProvider.QueryService(SIDGuid, IIDGuid, out serviceIntPtr);

            if (hr != 0)
            {
                System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(hr);
            }
            else if (!serviceIntPtr.Equals(IntPtr.Zero))
            {
                service = System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(serviceIntPtr);
                System.Runtime.InteropServices.Marshal.Release(serviceIntPtr);
            }

            return service;
        }

        private ProjectVersion LoadVersionNumber(EnvDTE.Project project, ProjectItem projectItem, bool hasCocoa, bool hasAndroid, bool hasUWP, bool isSdk)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            ProjectVersion result;


            result = (isSdk) ? ProcessNewStyleProject(project) : ProcessOldStyleProject(project, projectItem);

            if (result != null)
            {
                if (hasCocoa)
                {
                    result.IsCocoa = true;
                    result.ProjectType = "Xamarin";


                    var infoPlist = FindProjectItem(project.ProjectItems, "info.plist");
                    result.SecondaryProjectItem = infoPlist;

                    if (infoPlist != null && infoPlist.Document != null)
                        infoPlist.Document.Close();
                }
                else if (hasAndroid)
                {
                    result.IsAndroid = true;
                    result.ProjectType = "Xamarin";

                    var aManifest = FindProjectItem(project.ProjectItems, "androidmanifest.xml");
                    result.SecondaryProjectItem = aManifest;

                    if (aManifest != null && aManifest.Document != null)
                        aManifest.Document.Close();
                }
                else if (hasUWP)
                {
                    result.IsUWP = true;
                    result.ProjectType = "UWP";

                    var packageManifest = FindProjectItem(project.ProjectItems, "package.appxmanifest");
                    result.SecondaryProjectItem = packageManifest;

                    if (packageManifest != null && packageManifest.Document != null)
                        packageManifest.Document.Close();


                }

            }

            return result;

        }

        /// <summary>
        /// Process the old style csproj file
        /// </summary>
        /// <param name="project"></param>
        /// <param name="projectItem"></param>
        /// <returns></returns>
        private ProjectVersion ProcessOldStyleProject(Project project, ProjectItem projectItem)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string version = String.Empty;
            string fileVersion = String.Empty;
            string versionSuffix = String.Empty;

            if (!projectItem.IsOpen)
                projectItem.Open();

            var aDoc = projectItem.Document;

            TextDocument editDoc = (TextDocument)aDoc.Object("TextDocument");

            var objEditPt = editDoc.StartPoint.CreateEditPoint();
            objEditPt.StartOfDocument();

            var endPpint = editDoc.EndPoint.CreateEditPoint();
            endPpint.EndOfDocument();

            string searchText = "AssemblyVersion";
            string searchText2 = "AssemblyFileVersion";
            string searchText3 = "AssemblyInformationalVersion";
            string searchVstart = "(\"";
            

            var ednLine = endPpint.Line;

            while (objEditPt.Line <= ednLine)
            {
                
                var aLine = objEditPt.GetText(objEditPt.LineLength);

                if (!aLine.StartsWith("//")
                        && !aLine.StartsWith("'"))
                {

                    if (aLine.Contains(searchText))
                    {
                        //find the AssemblyVersion 
                        int locationStart = aLine.IndexOf(searchText);
                        string remaining = aLine.Substring((locationStart + searchText.Length));

                        //find the start of the version definition
                        locationStart = remaining.IndexOf(searchVstart);
                        remaining = remaining.Substring((locationStart + searchVstart.Length));

                        int locationEnd = remaining.IndexOf("\"");
                        version = remaining.Substring(0, locationEnd);

                    }


                    if (aLine.Contains(searchText2))
                    {
                        int locationStart = aLine.IndexOf(searchText2);
                        string remaining = aLine.Substring((locationStart + searchText2.Length));

                        //find the start of the version definition
                        locationStart = remaining.IndexOf(searchVstart);
                        remaining = remaining.Substring((locationStart + searchVstart.Length));

                        int locationEnd = remaining.IndexOf("\"");
                        fileVersion = remaining.Substring(0, locationEnd);

                    }

                    if (aLine.Contains(searchText3))
                    {
                        int locationStart = aLine.IndexOf(searchText3);
                        string remaining = aLine.Substring((locationStart + searchText3.Length));

                        locationStart = remaining.IndexOf(searchVstart);
                        remaining = remaining.Substring((locationStart + searchVstart.Length));

                        int locationEnd = remaining.IndexOf("\"");
                        versionSuffix = remaining.Substring(0, locationEnd);

                    }
                }

                if (!String.IsNullOrWhiteSpace(version)
                    && !String.IsNullOrWhiteSpace(fileVersion)
                     && !string.IsNullOrEmpty(versionSuffix))
                    break;

                if (objEditPt.Line == ednLine)
                {
                    break;
                }
                else
                {
                    objEditPt.LineDown();
                    objEditPt.StartOfLine();
                }


            }

            aDoc.Close();
            aDoc = null;

            if (version != String.Empty)
            {
                
                var newVersion = new ProjectVersion();
                newVersion.Name = project.Name;
                newVersion.Path = projectItem.FileNames[0];
                newVersion.RealProject = project;
                newVersion.ProjectItem = projectItem;
                newVersion.ProjectType = "Standard";

                try
                {
                    newVersion.AssemblyVersion = new Version(version);
                }
                catch
                {
                    newVersion.AssemblyVersion = new Version("1.0");
                }

                if (fileVersion != String.Empty)
                {
                    try
                    {
                        newVersion.FileVersion = new Version(fileVersion);
                    }
                    catch
                    {
                        newVersion.FileVersion = newVersion.AssemblyVersion;
                    }
                }

                if (string.IsNullOrEmpty(versionSuffix) == false)
                {
                    if (versionSuffix.Contains("-"))
                    {
                        var start = versionSuffix.IndexOf("-");
                        if (start < versionSuffix.Length)
                        {
                            versionSuffix = versionSuffix.Substring(start + 1);
                            if (Regex.IsMatch(versionSuffix, @"^[0-9a-zA-Z\-]+$"))
                            {
                                newVersion.VersionSuffix = versionSuffix;
                            }
                        }
                    }
                }

                return newVersion;
            }
            else
            {
                var newFailedProject = new FailedProject()
                {
                    Name = project.Name,
                    FailedAssemblyVersion = string.IsNullOrWhiteSpace(version),
                    FailedAssemblyFileVersion = string.IsNullOrWhiteSpace(fileVersion),
                };

                FailedProjects.Add(newFailedProject);
            }

            return null;
        }

        /// <summary>
        /// Process the new style CSProj file
        /// </summary>
        /// <param name="project"></param>
        /// <returns></returns>
        private ProjectVersion ProcessNewStyleProject(Project project)
        {
            var assemblyVersion = string.Empty;
            var fileVersion = string.Empty;
            var packageVersion = string.Empty;
            var versionSuffix = string.Empty;
            var version = string.Empty;
            var versionPrefix = string.Empty;
            var informationVersion = string.Empty;
            var mauiAppDisplayVersion = string.Empty;
            var mauiAppVersion = string.Empty;

            ThreadHelper.ThrowIfNotOnUIThread();

            foreach (Property aProp in project.Properties)
            {

                if (aProp.Name.ToLower().Equals("assemblyversion"))
                {
                    assemblyVersion = aProp.Value as string;
                }
                else if (aProp.Name.ToLower().Equals("fileversion"))
                {
                    fileVersion = aProp.Value as string;
                }
                else if (aProp.Name.ToLower().Equals("version"))
                {
                    version = aProp.Value as string;
                }
                else if (aProp.Name.ToLower().Equals("versionprefix"))
                {
                    versionPrefix = aProp.Value as string;
                }
                else if (aProp.Name.ToLower().Equals("packageversion"))
                {
                    packageVersion = aProp.Value as string;
                    if (packageVersion.Contains("-") && string.IsNullOrEmpty(versionSuffix))
                    {
                        var start = packageVersion.IndexOf("-");
                        if (start < packageVersion.Length) start += 1;
                        versionSuffix = packageVersion.Substring(start);
                    }
                }
                else if (aProp.Name.ToLower().Equals("versionsuffix"))
                {
                    versionSuffix = aProp.Value as string;
                }
            }


            // Read properties that DTE doesn't expose reliably for SDK-style projects
            // directly from the .csproj XML.
            var txt = File.ReadAllLines(project.FileName);

            foreach (var aLine in txt)
            {
                if (aLine.Contains("<InformationalVersion>"))
                    informationVersion = aLine.ValueForNode("InformationalVersion");
                else if (aLine.Contains("<VersionPrefix>"))
                    versionPrefix = aLine.ValueForNode("VersionPrefix");
                else if (aLine.Contains("<ApplicationDisplayVersion>"))
                    mauiAppDisplayVersion = aLine.ValueForNode("ApplicationDisplayVersion");
                else if (aLine.Contains("<ApplicationVersion>"))
                    mauiAppVersion = aLine.ValueForNode("ApplicationVersion");
                else if (aLine.Contains("<VersionSuffix>") && string.IsNullOrEmpty(versionSuffix))
                    versionSuffix = aLine.ValueForNode("VersionSuffix");
                else if (aLine.Contains("<AssemblyVersion>") && string.IsNullOrEmpty(assemblyVersion))
                    assemblyVersion = aLine.ValueForNode("AssemblyVersion");
                else if (aLine.Contains("<FileVersion>") && string.IsNullOrEmpty(fileVersion))
                    fileVersion = aLine.ValueForNode("FileVersion");
                else if (aLine.Contains("<Version>") && string.IsNullOrEmpty(version))
                    version = aLine.ValueForNode("Version");
                else if (aLine.Contains("<PackageVersion>") && string.IsNullOrEmpty(packageVersion))
                    packageVersion = aLine.ValueForNode("PackageVersion");
            }

            // Extract the pre-release suffix from whichever version field carries it,
            // in priority order: explicit VersionSuffix → InformationalVersion → Version → PackageVersion.
            if (string.IsNullOrEmpty(versionSuffix))
            {
                foreach (var candidate in new[] { informationVersion, version, packageVersion })
                {
                    if (string.IsNullOrEmpty(candidate)) continue;
                    var dashPos = candidate.IndexOf('-');
                    if (dashPos >= 0 && dashPos < candidate.Length - 1)
                    {
                        versionSuffix = candidate.Substring(dashPos + 1);
                        break;
                    }
                }
            }

            //is this using the new style csproj
            var newVersion = new ProjectVersion();
            newVersion.Name = project.Name;
            newVersion.Path = project.FileName;
            newVersion.RealProject = project;
            newVersion.IsNewStyleProject = true;
            newVersion.ProjectType = "SDK";
            newVersion.VersionSuffix = versionSuffix;
            newVersion.InformationalVersion = informationVersion;

            if (assemblyVersion != string.Empty)
            {               
                try
                {
                    newVersion.AssemblyVersion = new Version(assemblyVersion);
                }
                catch
                {
                   
                }

            }

            if (fileVersion != string.Empty)
            {
                try
                {
                    newVersion.FileVersion = new Version(fileVersion);
                }
                catch
                {
                    newVersion.FileVersion = newVersion.AssemblyVersion;
                }
            }

            if (version != string.Empty)
            {
                try
                {
                    newVersion.Version = new Version(version);
                }
                catch
                {
                    
                }
            }

            if (packageVersion != string.Empty)
            {
                try
                {
                    newVersion.PackageVersion = new Version(packageVersion);
                }
                catch
                {

                }
            }

            if (versionPrefix != string.Empty)
            {
                try
                {
                    newVersion.VersionPrefix = new Version(versionPrefix);
                }
                catch
                {

                }
            }

            if (mauiAppDisplayVersion != string.Empty)
            {
                try
                {
                    newVersion.MauiDisplayVersion = new Version(mauiAppDisplayVersion);
                }
                catch
                {

                }
            }

            if (mauiAppVersion != string.Empty)
            {
                try
                {
                    newVersion.MauiAppVersion = new Version(mauiAppVersion);
                }
                catch
                {

                }
            }

            return newVersion;
        }

        /// <summary>
        /// Returns true when the .csproj file uses the SDK-style format
        /// (<c>&lt;Project Sdk="…"&gt;</c>). Used to handle projects that carry
        /// a legacy project-type GUID in the .sln yet were migrated to SDK style.
        /// </summary>
        private static bool IsSdkStyleProjectFile(string projectFilePath)
        {
            if (string.IsNullOrEmpty(projectFilePath) || !File.Exists(projectFilePath))
                return false;

            foreach (var line in File.ReadLines(projectFilePath))
            {
                var trimmed = line.TrimStart();

                if (trimmed.Length == 0)
                    continue;

                // SDK-style root element: <Project Sdk="Microsoft.NET.Sdk...">
                if (trimmed.StartsWith("<Project", StringComparison.OrdinalIgnoreCase)
                    && trimmed.IndexOf("Sdk=", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                // Old-style root element: <Project ToolsVersion="…" xmlns="…">
                // Once we hit the root tag we know enough — stop scanning.
                if (trimmed.StartsWith("<Project", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return false;
        }

        #endregion

 
    }
}
