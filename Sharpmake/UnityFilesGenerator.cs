using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

namespace Sharpmake
{
    class UnityFilesGenerator
    {
        private int MaxFilesPerUnityFile = 0;
        private string IntermediatePath = "";
        private string UnityFilesDir = "";
        private List<string> Files = new List<string>();


        public UnityFilesGenerator(int maxFilesPerUnityFile, string intermediatePath)
        {
            MaxFilesPerUnityFile = maxFilesPerUnityFile;
            IntermediatePath = intermediatePath;
            UnityFilesDir = Path.Combine(IntermediatePath, "unity");

            ClearUnityFilesDir();
        }

        public void AddFile(string filePath)
        {
            Files.Add(filePath);
        }

        public List<string> Generate()
        {
            List<string> unityFiles = new List<string>();

            StringBuilder sb = new StringBuilder();
            int numUnityFilesAdded = 0;
            foreach (string file in Files)
            {
                AddInclude(sb, file);
                ++numUnityFilesAdded;

                if (MaxFilesPerUnityFile != 0 && numUnityFilesAdded > MaxFilesPerUnityFile)
                {
                    string unityFileFilename = Path.Combine(UnityFilesDir, $"unity_{unityFiles.Count}.cpp");
                    File.WriteAllText(unityFileFilename, sb.ToString());
                    sb.Clear();
                    unityFiles.Add(unityFileFilename);
                }
            }

            if (sb.Length > 0)
            {
                string unityFileFilename = Path.Combine(UnityFilesDir, $"unity_{unityFiles.Count}.cpp");
                File.WriteAllText(unityFileFilename, sb.ToString());
                sb.Clear();
                unityFiles.Add(unityFileFilename);
            }

            return unityFiles;
        }

        private void AddInclude(StringBuilder sb, string filePath)
        {
            sb.AppendLine("");
            sb.AppendLine($"#include \"{filePath.Replace('\\', '/')}\"");
            sb.AppendLine("");
        }

        private void ClearUnityFilesDir()
        {
            if (Directory.Exists(UnityFilesDir))
            {
                Directory.Delete(UnityFilesDir, recursive: true);
            }
            Directory.CreateDirectory(UnityFilesDir);
        }
    }
}
