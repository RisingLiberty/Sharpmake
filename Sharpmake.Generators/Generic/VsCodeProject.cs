using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace Sharpmake.Generators.Generic
{
    public partial class VsCodeProject : IProjectGenerator
    {
        private static string tasksFilename = Path.Combine(Directory.GetCurrentDirectory(), ".vscode", $"tasks{Util.GetSolutionExtension(DevEnv.vscode)}"); // the absolute path to the tasks.json file
        private static object fileAccessLock = new object(); // the lock around writing/reading from the tasks.json file

        private class GenerationContext : IGenerationContext
        {
            private Dictionary<Project.Configuration, Options.ExplicitOptions> _projectConfigurationOptions;
            private IDictionary<string, string> _cmdLineOptions;
            private IDictionary<string, string> _linkerCmdLineOptions;
            private Resolver _envVarResolver;

            public Builder Builder { get; }
            public bool PlainOutput { get { return true; } }
            public string ProjectPath { get; }
            public Project Project { get; }
            public string ProjectDirectory { get; }
            public string ProjectFileName { get; }
            public string ProjectDirectoryCapitalized { get; }
            public string ProjectSourceCapitalized { get; }

            public Compiler Compiler { get; }

            public Project.Configuration Configuration { get; set; }

            public IReadOnlyDictionary<Project.Configuration, Options.ExplicitOptions> ProjectConfigurationOptions => _projectConfigurationOptions;

            public void SetProjectConfigurationOptions(Dictionary<Project.Configuration, Options.ExplicitOptions> projectConfigurationOptions)
            {
                _projectConfigurationOptions = projectConfigurationOptions;
            }

            public DevEnv DevelopmentEnvironment => Configuration.Target.GetFragment<DevEnv>();
            public DevEnvRange DevelopmentEnvironmentsRange { get; }
            public Options.ExplicitOptions Options
            {
                get
                {
                    Debug.Assert(_projectConfigurationOptions.ContainsKey(Configuration));
                    return _projectConfigurationOptions[Configuration];
                }
            }
            public IDictionary<string, string> CommandLineOptions
            {
                get
                {
                    Debug.Assert(_cmdLineOptions != null);
                    return _cmdLineOptions;
                }
                set
                {
                    _cmdLineOptions = value;
                }
            }
            public IDictionary<string, string> LinkerCommandLineOptions
            {
                get
                {
                    Debug.Assert(_linkerCmdLineOptions != null);
                    return _linkerCmdLineOptions;
                }
                set
                {
                    _linkerCmdLineOptions = value;
                }
            }
            public Resolver EnvironmentVariableResolver
            {
                get
                {
                    Debug.Assert(_envVarResolver != null);
                    return _envVarResolver;
                }
                set
                {
                    _envVarResolver = value;
                }
            }

            public FastBuildMakeCommandGenerator FastBuildMakeCommandGenerator { get; }

            public GenerationContext(Builder builder, string projectPath, Project project, Project.Configuration configuration)
            {
                Builder = builder;

                ProjectPath = projectPath;
                ProjectDirectory = Path.GetDirectoryName(ProjectPath);
                ProjectFileName = Path.GetFileName(ProjectPath);
                Project = project;

                ProjectDirectoryCapitalized = Util.GetCapitalizedPath(ProjectDirectory);
                ProjectSourceCapitalized = Util.GetCapitalizedPath(Project.SourceRootPath);

                Configuration = configuration;
                Compiler = configuration.Target.GetFragment<Compiler>();
            }

            public void Reset()
            {
                CommandLineOptions = null;
                Configuration = null;
                EnvironmentVariableResolver = null;
            }

            public void SelectOption(params Options.OptionAction[] options)
            {
                Sharpmake.Options.SelectOption(Configuration, options);
            }

            public void SelectOptionWithFallback(Action fallbackAction, params Options.OptionAction[] options)
            {
                Sharpmake.Options.SelectOptionWithFallback(Configuration, fallbackAction, options);
            }

        }
        
        // A task can only do 1 of these things
        // A task is generated for reach type
        private enum TaskType
        {
            Build,
            Clean,
            Rebuild
        }

        // A taks specifies a command to run by vscode.
        // for more info see: https://code.visualstudio.com/docs/editor/tasks 
        private class Task
        {
            public string label { get; set; }
            public string type { get; set; }
            public string command { get; set; }
            public Dictionary<string, string> windows { get; set; }
            public string group { get; set; }

            public Task()
            { }
            public Task(TaskType taskType, GenerationContext context)
            {
                label = $"{taskType} {context.Project.Name} - {context.Configuration.Target.ProjectConfigurationName} - {context.Compiler}";
                type = "shell";
                group = "build";

                switch (taskType)
                {
                    case TaskType.Build:
                        command = context.Configuration.CustomBuildSettings.BuildCommand;
                        break;
                    case TaskType.Clean:
                        command = context.Configuration.CustomBuildSettings.CleanCommand;
                        break;
                    case TaskType.Rebuild:
                        command = context.Configuration.CustomBuildSettings.RebuildCommand;
                        break;
                    default:
                        break;
                }

                windows = new Dictionary<string, string>();
                windows["command"] = command;
            }
        }

        // The class that represents the entire tasks.json file
        private class VsTasks
        {
            public string version { get; set; }
            public List<Task> tasks { get; set; }
        }

        public void Generate(
        Builder builder,
        Project project,
        List<Project.Configuration> configurations,
        string projectFilePath,
        List<string> generatedFiles,
        List<string> skipFiles)
        {
            lock (fileAccessLock)
            {
                // Load the tasks file into memory
                string jsonBlob = File.ReadAllText(tasksFilename);
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true // Makes it a bit more human friendly
                };

                // Add new tasks to the vs tasks
                VsTasks vsTasks = JsonSerializer.Deserialize<VsTasks>(jsonBlob, options);
                foreach (var config in configurations)
                {
                    GenerationContext context = new GenerationContext(builder, projectFilePath, project, config);
                    vsTasks.tasks.Add(new Task(TaskType.Build, context));
                    vsTasks.tasks.Add(new Task(TaskType.Clean, context));
                    vsTasks.tasks.Add(new Task(TaskType.Rebuild, context));
                }

                // Serialize into a new json blob
                jsonBlob = JsonSerializer.Serialize(vsTasks, options);

                // Save the blob to disk
                MemoryStream memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(jsonBlob));
                FileInfo projectFileInfo = new FileInfo(tasksFilename);
                builder.Context.WriteGeneratedFile(project.GetType(), projectFileInfo, memoryStream);
            }
        }

        public void Generate(
            Builder builder,
            Solution solution,
            List<Solution.Configuration> configurations,
            string solutionFile,
            List<string> generatedFiles,
            List<string> skipFiles)
        {
            // Create the original content of the tasks.json file
            VsTasks vsTasks = new VsTasks();
            vsTasks.version = "2.0.0";
            vsTasks.tasks = new List<Task>();
            var options = new JsonSerializerOptions
            {
                WriteIndented = true // Makes it a bit more human friendly
            };

            // Serialise the content
            string jsonString = JsonSerializer.Serialize(vsTasks, options);
            MemoryStream memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(jsonString));
            FileInfo solutionFileInfo = new FileInfo(tasksFilename);

            // Write the content to disk
            if (builder.Context.WriteGeneratedFile(solution.GetType(), solutionFileInfo, memoryStream))
            {
                generatedFiles.Add(solutionFileInfo.FullName);
            }
            else
            {
                skipFiles.Add(solutionFileInfo.FullName);
            }
        }

    }
}
