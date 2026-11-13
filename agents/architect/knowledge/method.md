Working method that keeps runs short and plans valid:
- Start with query (snapshot.metrics, snapshot.buildings[category=storage], ...) and two or three
  get_region calls around the Town Center; do not scan the whole map.
- Ambition 3 and above: set_zones first, then one place_group per district. A district is one
  call, not thirty. Check its "unplaced" list: enlarge the zone, lower spacing or move blockers out.
- Ambition 1-2: single moves with find_sites and apply_to_draft are fine.
- Work district by district. After each district: score_draft. Keep a running list of applied action ids;
  every action you put in the plan must have been applied to the draft in that order.
- Roads: after the last placement call route_network with apply=true. It returns a few grouped
  road_add actions (one per area); put them in the phase that first needs them and its road_remove
  actions in Clean-up. Do not re-split them into single tiles.
- Native mode: call forecast_stock with your phases before submit_plan.
- submit_plan replays everything on a fresh copy of the village; an action that was only valid because of
  a later action fails. Keep the phase order identical to the draft order.
