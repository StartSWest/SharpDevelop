﻿// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.ComTypes;
using System.Xml.Linq;

using ICSharpCode.Core;
using ICSharpCode.NRefactory.TypeSystem;
using ICSharpCode.NRefactory.TypeSystem.Implementation;
using ICSharpCode.SharpDevelop.Dom;
using ICSharpCode.SharpDevelop.Parser;
using ICSharpCode.SharpDevelop.Project.Converter;

namespace ICSharpCode.SharpDevelop.Project
{
	public enum OutputType {
		[Description("${res:Dialog.Options.PrjOptions.Configuration.CompileTarget.Exe}")]
		Exe,
		[Description("${res:Dialog.Options.PrjOptions.Configuration.CompileTarget.WinExe}")]
		WinExe,
		[Description("${res:Dialog.Options.PrjOptions.Configuration.CompileTarget.Library}")]
		Library,
		[Description("${res:Dialog.Options.PrjOptions.Configuration.CompileTarget.Module}")]
		Module
	}
	
	/// <summary>
	/// A compilable project based on MSBuild.
	/// </summary>
	public abstract class CompilableProject : MSBuildBasedProject, IUpgradableProject
	{
		#region Static methods
		/// <summary>
		/// Gets the file extension of the assembly created when building a project
		/// with the specified output type.
		/// Example: OutputType.Exe => ".exe"
		/// </summary>
		public static string GetExtension(OutputType outputType)
		{
			switch (outputType) {
				case OutputType.WinExe:
				case OutputType.Exe:
					return ".exe";
				case OutputType.Module:
					return ".netmodule";
				default:
					return ".dll";
			}
		}
		#endregion
		
		/// <summary>
		/// A list of project properties that cause reparsing of references when they are changed.
		/// </summary>
		protected readonly ISet<string> reparseReferencesSensitiveProperties = new SortedSet<string>();
		
		/// <summary>
		/// A list of project properties that cause reparsing of code when they are changed.
		/// </summary>
		protected readonly ISet<string> reparseCodeSensitiveProperties = new SortedSet<string>();
		
		protected CompilableProject(ProjectCreateInformation information)
			: base(information)
		{
			this.OutputType = OutputType.Exe;
			this.RootNamespace = information.RootNamespace;
			this.AssemblyName = information.ProjectName;
			
			ClientProfileTargetFramework clientProfile = information.TargetFramework as ClientProfileTargetFramework;
			if (clientProfile != null) {
				SetProperty(null, null, "TargetFrameworkVersion", clientProfile.FullFramework.Name, PropertyStorageLocations.Base, true);
				SetProperty(null, null, "TargetFrameworkProfile", "Client", PropertyStorageLocations.Base, true);
			} else if (information.TargetFramework != null) {
				SetProperty(null, null, "TargetFrameworkVersion", information.TargetFramework.Name, PropertyStorageLocations.Base, true);
			}
			
			SetProperty("Debug", null, "OutputPath", @"bin\Debug\",
			            PropertyStorageLocations.ConfigurationSpecific, true);
			SetProperty("Release", null, "OutputPath", @"bin\Release\",
			            PropertyStorageLocations.ConfigurationSpecific, true);
			LoadConfigurationPlatformNamesFromMSBuild();
			
			SetProperty("Debug", null, "DebugSymbols", "True",
			            PropertyStorageLocations.ConfigurationSpecific, true);
			SetProperty("Release", null, "DebugSymbols", "False",
			            PropertyStorageLocations.ConfigurationSpecific, true);
			
			SetProperty("Debug", null, "DebugType", "Full",
			            PropertyStorageLocations.ConfigurationSpecific, true);
			SetProperty("Release", null, "DebugType", "None",
			            PropertyStorageLocations.ConfigurationSpecific, true);
			
			SetProperty("Debug", null, "Optimize", "False",
			            PropertyStorageLocations.ConfigurationSpecific, true);
			SetProperty("Release", null, "Optimize", "True",
			            PropertyStorageLocations.ConfigurationSpecific, true);
		}
		
		protected CompilableProject(ProjectLoadInformation information)
			: base(information)
		{
		}
		
		/// <summary>
		/// Gets the path where temporary files are written to during compilation.
		/// </summary>
		[Browsable(false)]
		public DirectoryName IntermediateOutputFullPath {
			get {
				string outputPath = GetEvaluatedProperty("IntermediateOutputPath");
				if (string.IsNullOrEmpty(outputPath)) {
					outputPath = GetEvaluatedProperty("BaseIntermediateOutputPath");
					if (string.IsNullOrEmpty(outputPath)) {
						outputPath = "obj";
					}
					outputPath = Path.Combine(outputPath, this.ActiveConfiguration.Configuration);
				}
				return Directory.CombineDirectory(outputPath);
			}
		}
		
		/// <summary>
		/// Gets the full path to the xml documentation file generated by the project, or
		/// <c>null</c> if no xml documentation is being generated.
		/// </summary>
		[Browsable(false)]
		public string DocumentationFileFullPath {
			get {
				string file = GetEvaluatedProperty("DocumentationFile");
				if (string.IsNullOrEmpty(file))
					return null;
				return Path.Combine(Directory, file);
			}
		}
		
		// Make Language abstract again to ensure backend-binding implementers don't forget
		// to set it.
		public abstract override string Language {
			get;
		}
		
		[Browsable(false)]
		public string TargetFrameworkVersion {
			get { return GetEvaluatedProperty("TargetFrameworkVersion") ?? "v2.0"; }
			set { SetProperty("TargetFrameworkVersion", value); }
		}
		
		[Browsable(false)]
		public string TargetFrameworkProfile {
			get { return GetEvaluatedProperty("TargetFrameworkProfile"); }
			set { SetProperty("TargetFrameworkProfile", value); }
		}
		
		public override string AssemblyName {
			get { return GetEvaluatedProperty("AssemblyName") ?? Name; }
			set { SetProperty("AssemblyName", value); }
		}
		
		public override string RootNamespace {
			get { return GetEvaluatedProperty("RootNamespace") ?? ""; }
			set { SetProperty("RootNamespace", value); }
		}
		
		/// <summary>
		/// The full path of the assembly generated by the project.
		/// </summary>
		public override FileName OutputAssemblyFullPath {
			get {
				string outputPath = GetEvaluatedProperty("OutputPath") ?? "";
				return Directory.CombineDirectory(outputPath).CombineFile(AssemblyName + GetExtension(OutputType));
			}
		}
		
		/// <summary>
		/// The full path of the folder where the project's primary output files go.
		/// </summary>
		public DirectoryName OutputFullPath {
			get {
				string outputPath = GetEvaluatedProperty("OutputPath");
				// CombineDirectory() cleans up any back references.
				// e.g. C:\windows\system32\..\system becomes C:\windows\system
				return Directory.CombineDirectory(outputPath);
			}
		}
		
		[Browsable(false)]
		public OutputType OutputType {
			get {
				try {
					return (OutputType)Enum.Parse(typeof(OutputType), GetEvaluatedProperty("OutputType") ?? "Exe", true);
				} catch (ArgumentException) {
					return OutputType.Exe;
				}
			}
			set {
				SetProperty("OutputType", value.ToString());
			}
		}
		
		protected override void OnActiveConfigurationChanged(EventArgs e)
		{
			base.OnActiveConfigurationChanged(e);
			if (!isLoading) {
				Reparse(true, true);
			}
		}
		
		protected override void OnPropertyChanged(ProjectPropertyChangedEventArgs e)
		{
			base.OnPropertyChanged(e);
			if (e.PropertyName == "TargetFrameworkVersion")
				CreateItemsListFromMSBuild();
			if (!isLoading) {
				bool reparseReferences = reparseReferencesSensitiveProperties.Contains(e.PropertyName);
				bool reparseCode = reparseCodeSensitiveProperties.Contains(e.PropertyName);
				Reparse(reparseReferences, reparseCode);
			}
		}
		
		void Reparse(bool references, bool code)
		{
			lock (SyncRoot) {
				if (projectContentContainer == null)
					return; // parsing hasn't started yet; no need to re-parse
				projectContentContainer.SetAssemblyName(this.AssemblyName);
				projectContentContainer.SetLocation(this.OutputAssemblyFullPath);
				if (references) {
					projectContentContainer.ReparseReferences();
				}
				if (code) {
					projectContentContainer.SetCompilerSettings(CreateCompilerSettings());
					projectContentContainer.ReparseCode();
				}
			}
		}
		
		public StartAction StartAction {
			get {
				try {
					return (StartAction)Enum.Parse(typeof(StartAction), GetEvaluatedProperty("StartAction") ?? "Project");
				} catch (ArgumentException) {
					return StartAction.Project;
				}
			}
			set {
				SetProperty("StartAction", value.ToString());
			}
		}
		
		protected override ProjectBehavior CreateDefaultBehavior()
		{
			return new DotNetStartBehavior(this, base.CreateDefaultBehavior());
		}
		
		
		public override void Dispose()
		{
			lock (SyncRoot) {
				if (projectContentContainer != null)
					projectContentContainer.Dispose();
			}
			base.Dispose();
		}
		
		#region IUpgradableProject
		[Browsable(false)]
		public virtual bool UpgradeDesired {
			get {
				return MinimumSolutionVersion < SolutionFormatVersion.VS2010;
			}
		}
		
		public virtual CompilerVersion CurrentCompilerVersion {
			get { return GetOrCreateBehavior().CurrentCompilerVersion; }
		}
		
		public virtual TargetFramework CurrentTargetFramework {
			get { return GetOrCreateBehavior().CurrentTargetFramework; }
		}
		
		public virtual IEnumerable<CompilerVersion> GetAvailableCompilerVersions()
		{
			return GetOrCreateBehavior().GetAvailableCompilerVersions();
		}
		
		public virtual IEnumerable<TargetFramework> GetAvailableTargetFrameworks()
		{
			return GetOrCreateBehavior().GetAvailableTargetFrameworks();
		}
		
		public virtual void UpgradeProject(CompilerVersion newVersion, TargetFramework newFramework)
		{
			if (!IsReadOnly)
				GetOrCreateBehavior().UpgradeProject(newVersion, newFramework);
		}
		
		public static FileName GetAppConfigFile(IProject project, bool createIfNotExists)
		{
			FileName appConfigFileName = Core.FileName.Create(Path.Combine(project.Directory, "app.config"));
			
			if (!File.Exists(appConfigFileName)) {
				if (createIfNotExists) {
					File.WriteAllText(appConfigFileName,
					                  "<?xml version=\"1.0\"?>" + Environment.NewLine +
					                  "<configuration>" + Environment.NewLine
					                  + "</configuration>");
				} else {
					return null;
				}
			}
			
			if (!project.IsFileInProject(appConfigFileName)) {
				FileProjectItem fpi = new FileProjectItem(project, ItemType.None, "app.config");
				ProjectService.AddProjectItem(project, fpi);
				FileService.FireFileCreated(appConfigFileName, false);
				ProjectBrowserPad.RefreshViewAsync();
			}
			return appConfigFileName;
		}
		#endregion
		
		#region Type System
		volatile ProjectContentContainer projectContentContainer;
		IAssemblyModel assemblyModel;
		
		protected void InitializeProjectContent(IProjectContent initialProjectContent)
		{
			lock (SyncRoot) {
				if (projectContentContainer != null)
					throw new InvalidOperationException("Already initialized.");
				projectContentContainer = new ProjectContentContainer(this, initialProjectContent);
				projectContentContainer.SetCompilerSettings(CreateCompilerSettings());
			}
		}
		
		protected virtual object CreateCompilerSettings()
		{
			return null;
		}
		
		public override IProjectContent ProjectContent {
			get {
				var c = projectContentContainer;
				return c != null ? c.ProjectContent : null;
			}
		}
		
		public override IAssemblyModel AssemblyModel {
			get {
				SD.MainThread.VerifyAccess();
				var pc = ProjectContent;
				if (assemblyModel == null) {
					assemblyModel = SD.GetRequiredService<IModelFactory>().CreateAssemblyModel(new ProjectEntityModelContext(this, ".cs"));
					if (pc != null && assemblyModel is IUpdateableAssemblyModel) {
						((IUpdateableAssemblyModel)assemblyModel).AssemblyName = AssemblyName;
						((IUpdateableAssemblyModel)assemblyModel).FullAssemblyName = ProjectContent.FullAssemblyName;
						// Add the already loaded files into the model
						foreach (var file in pc.Files) {
							((IUpdateableAssemblyModel)assemblyModel).Update(null, file);
						}
					}
				}
				
				// Update references on every access
				if (pc != null && assemblyModel is IUpdateableAssemblyModel) {
					((IUpdateableAssemblyModel)assemblyModel).References = pc.AssemblyReferences
						.Select(ResolveReference).Where(r => r != null).ToList();
				}
				
				return assemblyModel;
			}
		}
		
		DomAssemblyName ResolveReference(IAssemblyReference reference)
		{
			if (reference is IUnresolvedAssembly)
				return new DomAssemblyName(((IUnresolvedAssembly)reference).FullAssemblyName);
			if (reference is ProjectReferenceProjectItem) {
				var project = ((ProjectReferenceProjectItem)reference).ReferencedProject;
				if (project == null) return null;
				return new DomAssemblyName(project.ProjectContent.FullAssemblyName);
			}
			return null;
		}
		
		public override void OnParseInformationUpdated(ParseInformationEventArgs args)
		{
			var c = projectContentContainer;
			if (c != null)
				c.ParseInformationUpdated(args.OldUnresolvedFile, args.NewUnresolvedFile);
			// OnParseInformationUpdated is called inside a lock, but we don't want to raise the event inside that lock.
			// To ensure events are raised in the same order, we always invoke on the main thread.
			SD.MainThread.InvokeAsyncAndForget(delegate {
			                                   	if (assemblyModel is IUpdateableAssemblyModel) {
			                                   		((IUpdateableAssemblyModel)assemblyModel).Update(args.OldUnresolvedFile, args.NewUnresolvedFile);
			                                   	}
			                                   	ParseInformationUpdated(null, args);
			                                   });
		}
		
		public override event EventHandler<ParseInformationEventArgs> ParseInformationUpdated = delegate {};
		#endregion
	}
}
