# PROJECT_CONTEXT.md
### Current Design Project Context

This is a living document. Update it as the project evolves.

---

## Studio

**AIAA (AI-Accelerated Architects)** — MArch studio at RMIT Melbourne, led by Prof. Alisa Andrasek. The studio's core goal is to push design exploration through AI-powered scripting workflows. The design itself is a vehicle for demonstrating the process.

---

## Site

Block north of Victoria Street, RMIT City Campus, Carlton, Victoria. Within RMIT's "City North Social Innovation Precinct." Short-term development priority in RMIT's Living Places Plan (draft, 2025). The plan calls for long-life, loose-fit buildings suited to flexible contemporary learning, teaching, research, and workspace.

Approximate footprint: ~40m × 60m.

---

## Building

A mixed-use academic tower — AI hub, design/engineering/robotics labs, data centre, flexible studios, collaborative research spaces.

### Scale
- 10 to 14 floors above ground
- Underground level(s): data centre + robotics lab
- Large dramatic underground volumes, hinted at from surface via a formal rupture ("the crack")

### Program stack (subject to change)
- **Underground:** Data centre, heavy robotics lab. Expressed at surface as a ground-plane crack.
- **Ground + L1:** Major public atrium — gathering space, permeable, civic.
- **L2–L4:** Fabrication + robotics labs (high floor-to-ceiling, heavy floor loads).
- **L5–L10:** AI hub + research studios — reconfigurable floors (structure-as-wall system).
- **L11–L14:** Design studios, collaborative workspace, postgrad, roof pavilion.

### Reconfigurable floors
The research/studio floors must dynamically change configuration. A single floor operates as one 90-person space or divides into three 30-person spaces. The timber structure itself is the wall system — members thicken at predefined split planes, thin in open zones. The structure encodes the flexibility.

---

## Design Intent

### Primary tectonic language
Dense aggregation of timber members. Not a conventional structural grid — members aggregate along algorithmically generated paths, producing a field condition rather than a frame. Orthogonally biased clusters with directionality, gravity logic, and rotational variation.

### Three scales of design
1. **Internal space** — how floors, programs, and atrium relate
2. **Timber tectonic detail** — how members aggregate, connect, and vary (start here)
3. **External/environmental** — how the building meets the city and sky

### Design concepts in play
- Aggregation of slight variations across repeated prefab elements
- Multi-agent simulation influencing spatial organisation
- Voxel volume as building container, with organic component growth within
- Joint system — algorithmically generated faked joinery (visual/tectonic, not structural engineering)

### Key references (in `examples/` folder)
- Dense timber member aggregation images
- Wulfarchitekten Oberamteistraße Museum (exterior + interior timber frame)
- Alisa Andrasek's "Different Meta-Part Combinations" (voxel bounding box + variable internal geometry)

---

## What Scripts Are Needed

The Grasshopper workflow chains multiple ScriptNode components to explore design. The typical pipeline:

```
DataNode (per-level parameters)
  → Levels Script (floor geometry)
    → Voxel Grid (building volume) 
      → Boid Simulation (path generation within volume)
        → Component Placement (timber members along paths)
          → Joint Resolution (connection geometry at intersections)
```

But the power of ScriptNode is that this order is not fixed. Alternative chains:

```
Attractor Field → Voxel Activation → Direct Placement (no boids)
Perlin Noise → Surface Modulation → Envelope Generation
Boids → Path Curves → Ladybug Solar Analysis → Density Adjustment
DataNode (room configs) → Room Subdivision → Interior Layout
```

**DataNode** is the preferred way to supply per-item configuration (e.g. per-level heights, per-room areas) to ScriptNode scripts. It replaces the need for banks of individual sliders.

The flexibility to reorder and remix these systems is the point of the GH workflow. Write scripts as modular, chainable units — not as monolithic pipelines.

---

## Component Library (placeholder)

Timber member types for the aggregation system:
- Short member: ~200mm × 200mm × 600mm range
- Long member: ~200mm × 200mm × 3000mm range
- Sizes are parametric, not fixed
- Each member has predefined connection points at endpoints
- Joint types: lap, notch, cross — assigned by angle between meeting members

---

## What Is Not Decided Yet

- Specific timber member cross-sections
- Exact voxel resolution for the master building volume
- Number and type of agent behaviours in pathfinding
- Envelope/skin design language
- Joint profile library details
- Final floor count within 10–14 range
- Whether reconfigurable floor logic is structural or representational

---

*End of PROJECT_CONTEXT.md. Last updated: 2026-03-15.*
