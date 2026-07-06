# Changelog

All notable changes to the Ash Server project will be documented in this file.

## [Unreleased] - 2026-07-06

### Added
- **Database Status API**: Created a new endpoint `GET /api/admin/database/status` in [Controllers.cs](file:///C:/Users/ssfdr/ash-server/Controllers/Controllers.cs) to report the active database path and dynamic file size.
- **Database Backup Deletion API**: Created `DELETE /api/admin/database/backups/{filename}` in [Controllers.cs](file:///C:/Users/ssfdr/ash-server/Controllers/Controllers.cs) to clean up backups with path-traversal prevention.
- **Admin Database UI**: Integrated a dynamic database status widget, backup creation trigger, and list/delete backup actions in the Database tab of [admin.html](file:///C:/Users/ssfdr/ash-server/wwwroot/admin.html).

### Changed
- **Unified Configuration Loading**: Refactored the `GET /api/admin/config` endpoint in [Controllers.cs](file:///C:/Users/ssfdr/ash-server/Controllers/Controllers.cs) to merge default settings, `appsettings.json`, environment variables, and `config.json` overlays dynamically via `IConfiguration`. This prevents blank configuration values from rendering in the admin menu.
- **SQLite Relative Path Resolution**: Changed path checks in Database status, backup, and deletion endpoints to resolve via `Path.GetFullPath()` to correctly align with SQLite process current working directory execution.
- **Grid Coordinator & Worker Separation**: Refactored the Grid panel layout in [admin.html](file:///C:/Users/ssfdr/ash-server/wwwroot/admin.html) to separate Master settings and Worker forms into role-based toggle tabs, improving layout cleanliness.
- **Database Backup Response Schema**: Rewrote the `Backup()` endpoint in [Controllers.cs](file:///C:/Users/ssfdr/ash-server/Controllers/Controllers.cs) to return correctly structured JSON matching the UI expectations.

### Fixed
- **HTML Nesting Bug**: Fixed a missing closing `</div>` tag for `mcp-install-modal` (line 897 in [admin.html](file:///C:/Users/ssfdr/ash-server/wwwroot/admin.html)) which caused subsequent tab contents (`#config`, `#database`, `#updates`) to parse inside the hidden MCP tab and render blank/invisible.
- **Undefined Escape Helper**: Aliased `esc` to `escHtml` globally in [admin.html](file:///C:/Users/ssfdr/ash-server/wwwroot/admin.html#L1573) to prevent `ReferenceError: esc is not defined` from halting page initialization scripts.
