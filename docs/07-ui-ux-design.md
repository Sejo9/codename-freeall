# FreeAll --- UI/UX Design System

## 1. Design direction

FreeAll must look like a premium, contemporary Windows 11 desktop
product---not a legacy control-panel utility and not a generic web
dashboard placed inside a desktop window.

The visual language should be: - Modern Fluent-inspired Windows
design. - Calm, precise and information-dense without feeling crowded. -
Dark and light themes designed as first-class experiences. - Clear
hierarchy, consistent spacing and restrained depth. - Functional before
decorative. - Fast, responsive and accessible.

## 2. Product shell

### Main window

-   Left navigation rail/sidebar with icon and label.
-   Top title/action region with current page title, search and
    contextual actions.
-   Main content canvas with consistent margins.
-   Optional right-side inspector panel for selected file/folder
    details.
-   Bottom status region for active scan state, selected volume and
    background activity.
-   Responsive layout that can collapse the sidebar and inspector.

### Primary navigation

1.  Overview
2.  Drives
3.  Storage Explorer
4.  File Categories
5.  Large Files
6.  Application Residue
7.  Temporary Files
8.  Duplicate Files
9.  Windows Storage
10. Scan History
11. Activity
12. Settings

## 3. Visual system

### Design tokens

Define tokens centrally rather than hard-coding colours in individual
views: - `Color.Background` - `Color.Surface` -
`Color.SurfaceElevated` - `Color.SurfaceHover` - `Color.Border` -
`Color.TextPrimary` - `Color.TextSecondary` - `Color.Accent` -
`Color.Success` - `Color.Warning` - `Color.Danger` - `Color.Info` -
`Radius.Small`, `Radius.Medium`, `Radius.Large` - `Spacing.1` through
`Spacing.8` - `Typography.Display`, `Heading`, `Title`, `Body`,
`Caption`, `Monospace`

Use Windows accent colour as an optional accent source, but preserve
sufficient contrast. Status colours must never be the only signal.

### Typography

-   Use Segoe UI Variable where available, with a robust Segoe UI
    fallback.
-   Use tabular numerals for file sizes, counts and timestamps.
-   Use monospace only for paths, hashes, IDs and technical values.
-   Avoid oversized headings in data-heavy views.

### Surfaces and materials

-   Use restrained Mica-inspired window surfaces where technically
    appropriate.
-   Use opaque/elevated surfaces behind dense tables for readability.
-   Avoid excessive blur, transparency, gradients and shadows.
-   Respect reduced-motion and high-contrast settings.

### Shape and density

-   Consistent 6--10 px corner radii for common controls/cards, with
    design tokens.
-   Compact data density for expert workflows.
-   Generous spacing between major sections.
-   Avoid turning every row into a card.

## 4. Themes

-   Light.
-   Dark.
-   Follow Windows/system setting.
-   Optional accent customization.
-   Persist user choice.
-   Ensure every screen, dialog, chart, tooltip, context menu and empty
    state is themed consistently.

## 5. Screen specifications

### Overview

-   Storage summary cards.
-   Drive usage visualizations.
-   Last scan and scan completeness.
-   Largest growth areas and review candidates.
-   Recent activity.
-   No misleading "one-click speed up" or exaggerated cleanup score.

### Drives

-   Drive list with type, label, filesystem, capacity, used/free and
    scan state.
-   Explicit fixed/removable/network classification.
-   Scan and exclusion controls.
-   Volume identity available in details.

### Storage Explorer

-   Breadcrumbs and path context.
-   Virtualized tree and file list.
-   Columns: name, type, logical size, allocated size, modified,
    attributes, category.
-   Inspector panel with file identity, volume, classification and scan
    status.
-   Multi-select and review queue.

### Application Residue

-   Application identity and publisher where known.
-   Candidate path, evidence, confidence and risk.
-   Separate application-level and path-level decisions.
-   Keep/ignore/review controls.
-   Clear warning that user data/settings may be present.

### Duplicate Files

-   Group by exact content within a single volume.
-   Show size, number of entries, hard-link relationships and potential
    reclaim estimate.
-   No auto-selected "best copy" for deletion.

### Cleanup review

-   Exact target list and full paths.
-   Destination mode.
-   Risk explanation.
-   Revalidation status.
-   Explicit final confirmation.
-   Clear result summary.

### Quarantine

-   Original location and destination.
-   Verification status.
-   Restore action.
-   Conflict resolution.
-   No purge-by-default.

## 6. Interaction patterns

-   Use progressive disclosure for advanced metadata.
-   Keep destructive actions visually distinct from inspection actions.
-   Use explicit labels: "Move to Recycle Bin", "Quarantine",
    "Permanently delete".
-   Do not use ambiguous labels such as "Clean", "Fix" or "Optimize" for
    destructive operations.
-   Show progress with stage, current volume/path where appropriate,
    item counts and errors.
-   Allow cancellation and explain whether cancellation is immediate or
    occurs after the current safe unit.
-   Confirm only meaningful destructive decisions; do not over-confirm
    harmless navigation.
-   Provide undo/restore affordances where supported.

## 7. Accessibility

-   Full keyboard navigation and visible focus.
-   Logical tab order.
-   Automation names and accessible descriptions.
-   Screen-reader labels for charts and treemap alternatives.
-   High contrast support.
-   Text scaling and high DPI.
-   Minimum practical hit targets.
-   Never communicate status by colour alone.
-   Test with Narrator and Windows accessibility inspection tools.

## 8. Motion

-   Short transitions for navigation and panel changes.
-   Avoid animation during high-frequency scanning updates.
-   No decorative motion that competes with data.
-   Respect reduced-motion preferences.

## 9. Design acceptance

The design is accepted only after: - Light/dark coverage across all
screens. - High-DPI validation. - Accessibility review. - Large-data
interaction prototype. - Empty, loading, partial, error and success
states. - Destructive workflow usability review. - Consistent component
tokens and documentation.

## 10. Prototype gate

Before implementing the full UI, prototype: 1. A directory explorer with
a very large virtualized result set. 2. A duplicate group view. 3. A
treemap with progressive aggregation. 4. A multi-step cleanup review. 5.
A quarantine restore/conflict workflow.

Record performance and accessibility findings. Choose third-party
controls only after licensing, maintenance and compatibility review.
