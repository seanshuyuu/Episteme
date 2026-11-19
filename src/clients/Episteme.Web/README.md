# Episteme Web

This Blazor Server app implements the ordinary-user and administrator prototype pages. Run it from the repository root with:

```powershell
dotnet run --project src/clients/Episteme.Web
```

Configure the OpenID Connect provider and API before starting the app:

| Environment variable | Purpose |
| --- | --- |
| `Authentication__Authority` | Identity provider base URL |
| `Authentication__ClientId` | Public OIDC client registered for this app |
| `Authentication__ApiAudience` | API audience/resource requested during login |
| `Api__BaseUrl` | Absolute API base URL; defaults to the audience |
| `Authentication__MetadataAddress` | Optional discovery document URL |
| `Authentication__TokenEndpoint` | Optional token endpoint used for refresh |

Register the app's `/signin-oidc` callback URL with the identity provider for the selected launch profile. The app uses authorization code flow with PKCE and does not require a client secret. Supply configuration through the environment or a local, untracked secrets provider; do not commit credentials.

The document tree supports uploading up to 100 files at a time (1 MiB per file) into the selected folder, including folder uploads in browsers supporting directory selection. Browser folder selection does not include empty directories. Uploads do not overwrite existing paths; each created directory and uploaded file is a separate Git commit, so failures report how many files were uploaded. A selected file or the entire workspace can be downloaded; ZIP downloads are limited to 1,000 entries and 32 MiB of file content and fail rather than returning an incomplete archive if the workspace changes or contains unsupported entries.

Entering a workspace shows project cards. Each project is backed by an independent Git repository and exposes name, description, status, file count, folder count, and branch. Project cards support open, create, edit, archive/restore, and soft-delete actions. Opening a project loads its directory tree and opens the root README.md by default.

Folders can be promoted to independent projects. Promotion copies the folder and retains the source folder by default. Projects can be demoted into the workspace default project, where the source files are stored under the project slug and the source project is archived rather than deleted.

Uploads show progress by completed file, not bytes transferred. On wide screens, drag the panel grips or use the arrow keys to resize the document tree and history; on narrower screens panels stack. Restoring an older document version creates a new Git commit rather than removing or rewriting previous history.

Users with write access can drag a document or folder onto another folder or the workspace root to move it. Save any pending document edits first. Moves use the current Git HEAD for concurrency checks, do not overwrite existing paths, and create a Git commit.
