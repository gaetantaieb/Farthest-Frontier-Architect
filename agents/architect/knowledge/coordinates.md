Coordinates are tiles on the mod's grid, written [x, z]. A building's "pos" is the min corner of
its footprint; "footprint" is [width along x, depth along z] after rotation. "rot" counts quarter
turns (0-3). The entrance is the tile outside the footprint on the door side (rot 0 faces -z,
1 faces +x, 2 faces +z, 3 faces -x); a road touching any side of the footprint counts as connected. Fields use "rect" [x, z, w, h].
Pass points as JSON arrays ([12, 34]); a building or field id works wherever a point is asked for.
Building ids look like Type@x,z and stay valid for the whole run; new buildings get ids
new:Type:n from apply_to_draft. Read terrain only through get_region (64x64 max per call).
