1. Take in the following inputs:
    Project Name
2. Create a new repo in ADO for that Project
3. Clone the newly created repo
4. Add .gitignore for visual studio to the repo root
5. Create a new folder in the new repo for the project
6. Copy the project out of the monolith into the repo project folder
7. Create a new SLN from a template in the new repo root
    a. Pull the <ProjectId> value out of the project csproj
    b. Update the [=[ProjectName]=] to the project name supplied
    c. Update the [=[GUID]=] with the project id from step a
8. Create a new pipeline from a template in the new repo root
    a. Update the [=[ProjectName]=] to the project name supplied
9. Create nuspec for project in repo project folder
10. Remove all project references and direct dll (3rd party components) references
11. Update the csproj to use <PackageReference> instead of `packages.config`
12. Add references to nuget packages for projects and 3rd party components
13. Commit & Push
14. Add pipeline to the repo using the pipeline in the root
