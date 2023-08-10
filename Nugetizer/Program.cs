using Microsoft.VisualStudio.Services.Common;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.WebApi;
using LibGit2Sharp;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Azure.Pipelines.WebApi;

public class Program {
    public static GitHttpClient client;
    public static VssConnection connection;
    public static string accessToken = Environment.GetEnvironmentVariable("ADO_PAT");

    public static void Main(string monolithLocation, string projectToMove, string newLocation = "") {
        var url = new System.Uri("https://dev.azure.com/FirstAmCorp");
        connection = new VssConnection(url, new VssBasicCredential(string.Empty, accessToken));
        client = connection.GetClient<GitHttpClient>();

        if(!string.IsNullOrEmpty(newLocation)) {
            MigrateToRepo(monolithLocation, projectToMove, newLocation);
        } else {
            MigrateInPlace(monolithLocation, projectToMove);
        }
        
        return;
    }

    public static void MigrateToRepo(string monolithLocation, string projectToMove, string newLocation) {
        var repo = CreateRepository(projectToMove);
        var pathToRepo = Path.Combine(newLocation, projectToMove);
        var pathToProject = Path.Combine(pathToRepo, projectToMove);

        if(!CloneRepository(pathToRepo, repo.WebUrl)) {
            return;
        }

        if(!StrangleFromMonolith(monolithLocation, pathToRepo, pathToProject)) {
            return;
        }

        try {
            AddTemplatedFiles(pathToRepo, pathToProject, projectToMove);

            TransitionToPackageReference(newLocation, projectToMove);
            
            CommitAndPush(newLocation, projectToMove);

            UploadPipeline(newLocation, projectToMove, repo);
        }
        catch(Exception e) {
            Console.WriteLine($"Exiting due to error: {e.Message}");
            Directory.Delete(pathToRepo, true);
        }
    }

    public static void MigrateInPlace(string monolithLocation, string projectToMove) {
        var repoLocation = monolithLocation;

        while(Directory.GetDirectories(repoLocation).Any(a => a.EndsWith(".git"))) {
            repoLocation = Directory.GetParent(repoLocation).FullName;
        }
        
        var pathToRepo = Path.Combine(repoLocation, projectToMove);
        var pathToProject = Path.Combine(pathToRepo, projectToMove);
        
        AddTemplatedFiles(pathToRepo, pathToProject, projectToMove);

        TransitionToPackageReference(monolithLocation, projectToMove);
        
        CommitAndPush(repoLocation, "");

        var repo = CreateRepository("FAST");

        UploadPipeline(monolithLocation, projectToMove, repo);
    }

    public static void UploadPipeline(string newLocation, string projectToMove, GitRepository repo) {
        var pipelineClient = connection.GetClient<PipelinesHttpClient>();

        var uploadParameters = new CreatePipelineParameters {
            Name = projectToMove,
            Folder = "/",
            Configuration = new CreateYamlPipelineConfigurationParameters {
                Path = "pipeline.yml",
                Repository = new CreateAzureReposGitRepositoryParameters {
                    Id = repo.Id,
                    Name = repo.Name
                }
            }
        };

        var pipeline = pipelineClient.CreatePipelineAsync(uploadParameters, "FAST").Result;

        var runParameters = new RunPipelineParameters {};

        pipelineClient.RunPipelineAsync(runParameters, "FAST", pipeline.Id).Wait();
    }

    public static void CommitAndPush(string newLocation, string projectToMove) {
        var projectPath = Path.Combine(newLocation, projectToMove);
        var repo = new LibGit2Sharp.Repository(projectPath);

        StageChanges(repo);
        CommitChanges(repo);
        PushChanges(repo);
    }
    
    public static void StageChanges(LibGit2Sharp.Repository repo) {
        try {
            var status = repo.RetrieveStatus();
            var files = new List<string>();
            var modified = status.Modified.Select(s => s.FilePath).ToList();
            var untracked = status.Untracked.Select(s => s.FilePath).ToList();

            files = modified.Concat(untracked).ToList();
            
            foreach(var filePath in files) {
                repo.Index.Add(filePath);
            }

            repo.Index.Write();
        }
        catch (Exception ex) {
            Console.WriteLine("Exception:RepoActions:StageChanges " + ex.Message);
        }
    }

    public static void CommitChanges(LibGit2Sharp.Repository repo) {
        try {
            repo.Commit("Migration from FAST Monolith", new Signature("Nugetizer", "nugetizer@firstam.com", DateTimeOffset.Now),
                new Signature("Nugetizer", "nugetizer@firstam.com", DateTimeOffset.Now));
        }
        catch (Exception e) {
            Console.WriteLine("Exception:RepoActions:CommitChanges " + e.Message);
        }
    }

    public static void PushChanges(LibGit2Sharp.Repository repo) {
        try {
            var remote = repo.Network.Remotes["origin"];
            var pushRefSpec = @"refs/heads/master";
            var options = new PushOptions {
                CredentialsProvider = (_url, _user, _cred) => new UsernamePasswordCredentials {
                    Username = "Anything",
                    Password = accessToken
                }
            };
            repo.Network.Push(remote, pushRefSpec, options);
        }
        catch (Exception e) {
            Console.WriteLine("Exception:RepoActions:PushChanges " + e.Message);
        }
    }

    static void TransitionToPackageReference(string newLocation, string projectToMove) {
        var pathToRepo = Path.Combine(newLocation, projectToMove);
        var pathToProject = Path.Combine(pathToRepo, projectToMove);
        var pathToCSProj = FindProjectFile(pathToProject, projectToMove);
        var doc = new XmlDocument();
        var removeNodes = new List<(XmlNode, XmlNode)>();

        doc.Load(pathToCSProj);
        
        var nsmgr = new XmlNamespaceManager(doc.NameTable);
        var ns = "http://schemas.microsoft.com/developer/msbuild/2003";

        nsmgr.AddNamespace("x", ns);

        var nodes = doc.SelectNodes("//*/x:Reference", nsmgr);

        foreach(XmlNode node in nodes) {
            var hintPath = node.SelectSingleNode("x:HintPath", nsmgr);

            if(hintPath == null) {
                continue;
            }

            if(File.Exists(Path.Combine(@"C:\Users\eeikenberry\source\repos\FAST\Source\FAF20\BT\BO\BOFrame", hintPath.InnerText))) {
                removeNodes.Add((node.ParentNode, node));
            }
        }

        nodes = doc.SelectNodes("//*/x:ProjectReference", nsmgr);

        foreach(XmlNode node in nodes) {
            removeNodes.Add((node.ParentNode, node));
        }

        var newItemGroup = doc.CreateNode(XmlNodeType.Element, "ItemGroup", ns);

        foreach(var nodeSet in removeNodes) {
            nodeSet.Item1.RemoveChild(nodeSet.Item2);
            
            var newReference = doc.CreateNode(XmlNodeType.Element, "PackageReference", ns);

            var includeAttribute = doc.CreateAttribute("Include");

            if(nodeSet.Item2.Attributes[0].Value.Contains(",")) {
                includeAttribute.Value = nodeSet.Item2.Attributes[0].Value.Split(",")[0];
            } else {
                includeAttribute.Value = Path.GetFileNameWithoutExtension(nodeSet.Item2.Attributes[0].Value);
            }

            var versionAttribute = doc.CreateAttribute("Version");

            versionAttribute.Value = "1.*";

            newReference.Attributes.Append(includeAttribute);
            newReference.Attributes.Append(versionAttribute);

            newItemGroup.AppendChild(newReference);
        }

        doc.SelectSingleNode("x:Project", nsmgr).AppendChild(newItemGroup);

        doc.Save(pathToCSProj);
    }

    static void CopyDirectory(string sourceDir, string destinationDir, bool recursive)
    {
        // Get information about the source directory
        var dir = new DirectoryInfo(sourceDir);

        // Check if the source directory exists
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");

        // Cache directories before we start copying
        DirectoryInfo[] dirs = dir.GetDirectories();

        // Create the destination directory
        Directory.CreateDirectory(destinationDir);

        // Get the files in the source directory and copy to the destination directory
        foreach (FileInfo file in dir.GetFiles())
        {
            string targetFilePath = Path.Combine(destinationDir, file.Name);
            file.CopyTo(targetFilePath);
        }

        // If recursive and copying subdirectories, recursively call this method
        if (recursive)
        {
            foreach (DirectoryInfo subDir in dirs)
            {
                string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
                CopyDirectory(subDir.FullName, newDestinationDir, true);
            }
        }
    }

    public static bool StrangleFromMonolith(string monolithLocation, string pathToRepo, string pathToProject) {
        if(Directory.Exists(pathToProject)) {
            Console.WriteLine("Exiting: Destination directory already exists");
            return false;
        }

        Directory.CreateDirectory(pathToProject);

        try {
            CopyDirectory(monolithLocation, pathToProject, true);
        } catch(Exception e) {
            Console.WriteLine("Exiting: Error migrating files from the monolith");
            Directory.Delete(pathToRepo, true);
            return false;
        }
        

        return true;
    }

    public static void AddTemplatedFiles(string pathToRepo, string pathToProject, string projectToMove) {
        File.Copy("./templates/.gitignore", Path.Combine(pathToRepo, ".gitignore"));

        var template = File.ReadAllText("./templates/nuget.nuspec");

        template = template.Replace("[=[ProjectName]=]", projectToMove);

        File.WriteAllText(Path.Combine(pathToProject, $"{projectToMove}.nuspec"), template);

        template = File.ReadAllText("./templates/pipeline.yml");

        template = template.Replace("[=[ProjectName]=]", projectToMove);

        File.WriteAllText(Path.Combine(pathToRepo, "pipeline.yml"), template);

        template = File.ReadAllText("./templates/solution");

        template = template.Replace("[=[ProjectName]=]", projectToMove);

        var guid = ExtractProjectGuid(pathToProject, projectToMove);

        template = template.Replace("[=[GUID]=]", guid);

        File.WriteAllText(Path.Combine(pathToRepo, $"{projectToMove}.sln"), template);
    }

    public static string FindProjectFile(string pathToProject, string projectToMove) {
        var projectFilePath = Path.Combine(pathToProject, $"{projectToMove}.csproj");

        if(!File.Exists(projectFilePath)) {
            var found = Directory.GetFiles(pathToProject, "*.csproj").FirstOrDefault();

            if(found != null) {
                projectFilePath = found;
            } else {
                throw new Exception();
            }
        }

        return projectFilePath;
    }

    public static string ExtractProjectGuid(string pathToProject, string projectToMove) {
        var projectFilePath = FindProjectFile(pathToProject, projectToMove);

        var projectFile = File.ReadAllText(projectFilePath);

        var regex = new Regex(@"\<ProjectGuid\>(.*)\<\/ProjectGuid\>");

        return regex.Match(projectFile).Groups[1].Value;
    }

    public static GitRepository CreateRepository(string projectName) {
        GitRepository repository = new GitRepository();

        try {
            repository = client.GetRepositoryAsync("FAST", projectName).Result;
        } catch {}

        if(repository.Id != Guid.Empty) {
            return repository;
        }

        repository = new GitRepository {
            Name = projectName
        };

        return client.CreateRepositoryAsync(repository, project: "FAST").Result;
    }

    public static bool CloneRepository(string path, string uri) {
        if(Directory.Exists(Path.Combine(path, ".git"))) {
            Console.WriteLine("Exiting: A repository already exists at the specified location");
            return false;
        }

        var options = new CloneOptions {
            CredentialsProvider = (_url, _user, _cred) => new UsernamePasswordCredentials {
                Username = "Anything",
                Password = accessToken
            }
        };

        LibGit2Sharp.Repository.Clone(uri, path, options);

        return true;
    }
}