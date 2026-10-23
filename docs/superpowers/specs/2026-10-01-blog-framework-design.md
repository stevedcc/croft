# Personal Blog Framework — Design Spec

## 1. Goal

Build a personal blogging application that runs on a self-hosted Kubernetes cluster. The system must be simple to operate, easy to test, and support Markdown-based writing with a few publishing conveniences.

## 2. High-level architecture

A single .NET solution with three projects:

| Project | Type | Responsibility |
|---------|------|----------------|
| `Blog.Api` | ASP.NET Core Web API | Owns the PostgreSQL database via EF Core (Npgsql). Exposes all business logic through REST endpoints. |
| `Blog.Web` | Server-side Blazor | MudBlazor UI. No direct database access; calls `Blog.Api` over HTTP. |
| `Blog.Shared` | Class library | DTOs, enums, and validation contracts shared by API and Web. |

The Blazor server app handles user authentication via OIDC from Authentik and forwards the access token to the API on every request. The API validates JWTs from the same Authentik application.

## 3. Technology choices

- **Runtime:** .NET 10
- **Database:** PostgreSQL 18, accessed with **EF Core** + `Npgsql.EntityFrameworkCore.PostgreSQL`
- **UI:** Server-side Blazor + MudBlazor
- **Auth:** Authentik via OpenID Connect
- **Search:** PostgreSQL full-text search (`tsvector`)
- **Scheduling:** `IHostedService` with a polling timer
- **Media storage:** Local filesystem in a mounted volume/PVC
- **Local dev:** Docker Compose for PostgreSQL
- **Deployment:** Plain Kubernetes YAML manifests

## 4. Data model

### 4.1 Entities

- **Post**
  - `Id` (UUID / int)
  - `Slug` (unique)
  - `Title`
  - `MarkdownContent`
  - `Excerpt`
  - `Status` enum: `Draft`, `Scheduled`, `Published`
  - `PublishedAt`
  - `CreatedAt`
  - `UpdatedAt`
  - `AuthorId`
  - `AuthorName`
  - `BannerImageId` (optional, one banner image)
  - `ViewCount` (denormalized counter)
  - `SearchVector` (tsvector, maintained by EF Core)
  - *Inline images are referenced by URL inside the Markdown content and can use any uploaded media asset.*

- **Tag**
  - `Id`
  - `Name`
  - `Slug`

- **Category**
  - `Id`
  - `Name`
  - `Slug`

- **MediaAsset**
  - `Id`
  - `FileName` (stored filename)
  - `OriginalName`
  - `ContentType`
  - `FilePath`
  - `UploadedAt`
  - `UploadedBy`
  - `Scope` enum: `Global` or `PostSpecific`
  - `PostId` (null for global assets)

- **Feedback** (Editor notes on drafts)
  - `Id`
  - `PostId`
  - `AuthorId`
  - `AuthorName`
  - `QuotedText` (the selected text from the post being commented on)
  - `Comment` (the editor’s note)
  - `CreatedAt`
  - `ResolvedAt` (null until addressed)

- **PageView**
  - `Id`
  - `PostId`
  - `Timestamp`
  - `IpHash` (SHA-256, for deduplication without storing raw IPs)
  - `UserAgent`
  - `Referrer`

### 4.2 Relationships

- `Post` many-to-many `Tag`
- `Post` many-to-many `Category`
- `Post` one-to-many `PageView`
- `Post` one-to-many `Feedback`
- `Post` one-to-many `MediaAsset` (post-scoped inline/banner images)
- `Post` optional one-to-one `MediaAsset` (banner image)

## 5. Authentication and authorization

Authentik returns a `role` claim. The application maps it to ASP.NET Core roles:

- **Admin** — create/edit/delete posts, publish, manage media, view analytics, manage tags/categories.
- **Editor** — read all drafts, create/edit own drafts, leave feedback with quoted sections, cannot publish or delete other users’ content. Intended for an AI feedback service account.

### 5.1 Auth schemes

- `Blog.Web`: cookie-based OIDC authentication (`AddOpenIdConnect`).
- `Blog.Api`: JWT bearer authentication (`AddJwtBearer`) validating tokens from Authentik.
- Authorization attributes: `[Authorize(Roles = "Admin")]`, `[Authorize(Roles = "Editor,Admin")]`, etc.

## 6. API surface

| Method | Route | Auth | Purpose |
|--------|-------|------|---------|
| GET | `/api/posts` | Public (only published) | Paged list with search/filter |
| GET | `/api/posts/{slug}` | Public for published, Editor/Admin for drafts | Single post |
| POST | `/api/posts` | Admin | Create post |
| PUT | `/api/posts/{id}` | Admin/Editor (own drafts) | Update post |
| DELETE | `/api/posts/{id}` | Admin | Delete post |
| POST | `/api/posts/{id}/publish` | Admin | Publish immediately or schedule |
| GET | `/api/posts/{id}/feedback` | Admin/Editor | Feedback on a draft |
| POST | `/api/posts/{id}/feedback` | Editor | Add feedback with quoted section |
| DELETE | `/api/posts/{id}/feedback/{feedbackId}` | Admin/Editor (own) | Remove feedback |
| GET | `/api/tags` | Public | List tags |
| POST | `/api/tags` | Admin | Create tag |
| GET | `/api/categories` | Public | List categories |
| POST | `/api/categories` | Admin | Create category |
| POST | `/api/media?postId={postId}&scope={global|post}` | Admin | Upload file (optionally scoped to a post) |
| GET | `/api/media/{id}` | Public | Download/view file |
| GET | `/api/posts/{id}/media` | Admin/Editor | List media scoped to a post |
| GET | `/api/search` | Public | Full-text search |
| GET | `/api/analytics` | Admin | Views over time |
| POST | `/api/analytics/pageview` | Public | Record a view (called by public site) |

## 7. Blazor UI

### 7.1 Public pages

- Home / post list with pagination
- Post detail (renders Markdown to HTML)
- Tag / category filtered lists
- Search results

### 7.2 Admin pages

- Post editor with Markdown input + live preview
  - Upload banner image
  - Upload inline images, choosing whether they are global or post-scoped
- Media library (global assets, plus per-post assets when editing a post)
- Tag/category management
- Review editor feedback
- Analytics dashboard

### 7.3 Editor pages

- Draft list
- Draft reader with inline feedback/commenting (select text → add note)
- Feedback list

## 8. Background jobs

### 8.1 Scheduled publisher

A hosted service polls every minute for posts where `Status == Scheduled && PublishedAt <= UtcNow` and transitions them to `Published`.

### 8.2 Analytics rollup (optional, v2)

Hourly aggregation of `PageView` rows into per-post daily counts to keep analytics reads fast.

## 9. Local development

- `compose.yaml` spins up PostgreSQL 18 on the local Docker network.
- `Blog.Api` and `Blog.Web` run from the IDE or `dotnet run`.
- Connection strings and OIDC secrets come from `user-secrets` or environment variables; nothing secret is committed.

## 10. Kubernetes deployment

- **Namespaces:** `blog`
- **Workloads:**
  - `blog-api` Deployment
  - `blog-web` Deployment
- **Database:** A **CloudNativePG (CNPG)** `Cluster` running PostgreSQL 18 in the `blog` namespace
- **Storage:**
  - Longhorn PVC for media uploads mounted at `/app/uploads`
  - CNPG-managed Longhorn PVCs for database storage
- **Config:**
  - ConfigMap: ASP.NETCORE_ENVIRONMENT, API base URL, Authentik authority/audience
  - Secret: DB connection string, OIDC client secret
- **Networking:**
  - Ingress routes `blog.example.com` → `blog-web`
  - Ingress routes `api.blog.example.com` → `blog-api`
  - CNPG provides `blog-db-rw` / `blog-db-ro` services inside the cluster

No Aspire, Helm, or complex service mesh is used. Plain YAML keeps the operational surface small for a single-user blog.

## 11. Non-goals

- Reader comments (legal complexity in the EU)
- RSS/Atom feeds (considered unnecessary)
- Multi-tenant authoring
- External object storage in v1 (local PVC is sufficient)

## 12. Success criteria

- Admin can create, edit, schedule, and publish Markdown posts.
- Editor can read drafts and leave feedback with quoted sections.
- Public visitors can browse/search published posts.
- Media uploads work in both local dev and Kubernetes.
- Analytics show view counts without storing raw IPs.
- The application deploys cleanly to Kubernetes using the provided manifests.
