# Nugetizer

This console application will take a csproj out of a monolithic repo and place it into a newly created repo.  It will also set up and run a new ADO pipeline to build the project as a nuget package.

# Usage

You must have an ADO Personal Access Token set up that can Pull, Push, Create Repos, Create and Run pipelines.  This must be set in an environment variable named `ADO_PAT`.

Supply the following parameters:
--monolith-location [location to project inside the monolith]
--new-location [location to place the new repo locally]
--project-to-move [name of the project to move]

Example:
`Nugetizer --monolith-location C:/repos/monolith/projectA --new-location C:/repos --project-to-move projectA`

# Notes
* Right now this is a little specific to First American b/c the ADO url and project name are not configurable.  Could easily make them command line options to
* If you do not wish to migrate your package to a new repo, omit the `--new-location` option
