# Precision Stockpile Control
This serves as the first "goal-orientated" design document for Precise Stockpile Control (PSC), which seeks to give players more tools to handle their stockpiles. By stockpiles we mean not just stockpiles on the ground but shelves and other mod-added storage containers as well.

## Overall Design Goals
Here are some overall design goals that PSC must adhere to:

### Low performance impact
Stockpiles are ubiquitous in any colony, thus we must ensure that we are not running checks on hotpaths constantly. We must stagger checks, run them in the background, anything we can do to improve performance; it has to be a design focus from the beginning.

### Intuitive Depth-Scaling UI
Players deal with stockpiles a great deal, but they won't need the extra PSC tools for every stockpile. It is important to avoid cluttering the stockpile menu or making creating basic stockpiles more tedious. Thus, we want a *depth-scaling* philosophy for the UI. What this means is that the initial controls as presented are minimalist; a player does not see all controls for every stockpile, but has to open them up and dive down in to open up more options. This helps keep things organized and decluttered; the player sees what they need and doesn't need to search through five options to get what they want. The key design constraint is that PSC will add only one button to default stockpiles, which opens its own submenu then.

However, in the spirit of keeping the player informed, we must let PSC effects be visible to the player. If they select an option, it must be made clear in the normal vanilla menu, without being obtrusive. This keeps the player informed and ensures that they never have to dive into the menu to see what is already running, it is visible at a glance.

### Save Game Compatibility
We must ensure that PSC is safe to add or remove from an existing save. Warnings are ok, but we have to be certain that if a player removes it from an in-progress colony, they can continue to play the colony after receiving those one-time warnings and do not get repeated errors on loads or NRE issues. Likewise, if a player adds PSC to an existing save, *nothing should change* with their current game: PSC loads in and does nothing until they interact with it and start using its tools.

We should also attempt to enable migration from other mods (see below) that enable similar settings. Stockpile limits in particular are already in use by players with various mods, and if we can port their per-stockpile settings over cleanly then we will make it much more user-friendly. We do NOT have to ensure that they can port back though if this is the case; we advise them to backup their saves!

### Stable Mod Integration
We must ensure that PSC plays safely and well with other mods that affect stockpiles and hauling. Critical ones:

Ogre stack (and related mods) that affect stockpile sizes. We must dynamically see what stockpile sizes actually are, not assume them or rely on vanilla.
Pick Up and Haul that changes how pawns haul. We have to present vanilla-like jobs so that play well with these other mods.
Flickable Storage that enables turning stockpiles on/off or to accept/receive only (possibly we integrate it directly).
Material Filter (Harmony) is our own mod that enables filtering by material for stockpiles; we should respect that, and integrate it directly so both work together seamlessly.
Other stockpile limit mods we should support the migration from for existing save games.

# Precise Stockpile Control Tools
The main control panel for PSC is opened by a button on the side of the stockpile, similar to what we do for Material Filter (Harmony). This then gives a main UI window from which we can deal with other controls.

## Stack Limits
This is the core feature. In vanilla RimWorld, stockpiles either accept an item or they do not; this means that if they accept items A and B, they might fill up with A and leave no room for B. They also fill up to the max, so a stockpile that can take 50 of an item when you only want 5 is vastly over supplying it. Finally, any missing item creates a haul job, so a pawn may try to fill a stockpile that's in use with one item after every use, causing repeated small jobs.

Stack limits consumes the majority of the PSC control window. 

### Limits
To fix this, we add limits. There is first a lower limit. This lower limit gives the limit at which an item will start to be refilled, when it is at or below this amount (so setting the lower limit to 0 ensure it is always refilled, default 0). Next, we add an upper limit. This upper limit gives the maximum amount of the item to include. So if the limits were set to 5-20, they would only start refilling at 5, and only up to 20 (so infinity/no setting is the default).

### Stack Limits
RimWorld uses the concept of "stacks" where items compact together into one stack, so e.g. two stacks of 25 steel can be compacted into one stack of 50. We have "stick limits" so e.g. that steel can be 75 maximum, and any extra has to go into a new stack. Stockpiles work off of stacks, not actual numbers of items, so e.g. two stacks of 10 of item A take as much space as two stacks of 75 for item B. Thus, controlling stockpile storage is about *numbers* and *stacks* together. Important: we must make the *stacks* clear to the player without necessarily working in stacks themselves.

CRITICAL: We always track items by items, not stacks! Stacks are just a convenient player-facing format. Setting "1 stack" is actually setting "1 stack-limit's worth" under the hood.

### UI
To enable stack limits, we break it into two pathways: global applications and per-item applications.

We always the player item limits in the format A (B)-X (Y), where A is the lower limit, B is the number of stacks it takes to contain that (so ceil(A/stack limit)), X is the upper limit and Y the stacks to contain X. If A or X is zero, we can drop that side and just show the dash, i.e. A (B)- or -X (Y).

When a player opens the stockpile and clicks the PSC button, they will get the subwindow, and it shows buttons "apply to search" and "remove from search", along with the entry point below that. The entry point has two modes, set by the toggle button on the left-hand side: items, or stacks. There is a slider to the right of that which allows a player to right-click on the left- and right-hand numbers to set directly, or they can use the slider. The slider in stacks mode has 0 the minimum and the maximum "unlimited", with the point just below maximum being the current maximum number of stacks allowed in the stockpile-1 (stockpiles can change size later, so "leave at infinite" will still keep it unlimited). Alternatively, if it's in items mode, it instead is setting the number of items directly. The slider in this case should have some "stickiness" at stack limits; this is just a UI factor, not an actual stickiness; we apply a buffer of 10% of the stack size again, so for example, if the stack size is 75, we go smoothly from 0 to 75, then by the UI we have to move a bit further to 75+~7.5 to reach 76, as this would require a new stack. We can apply the same buffer at the end, so that we get "maximum current number", a buffer, then "unlimited" at the far end.

The control window integrates with the vanilla search panel, so that when the player searches for items, and uses the "apply to search" button, it applies the selected limits to all items captured in the search. The "remove from search" removes the limits from the search panel, setting the items back to disallowed.

Now, to apply or change items individually, we use the vanilla window. By default in vanilla, clicking on an item's checkmark/X mark toggles whether it is allowed in the stockpile or not, and we respect that, and leave it alone. We let the player right-click on the toggle to open the limits submenu, which reproduces the global application menu as above, with "apply" and "cancel" buttons. Hitting apply then applies the selected limit to that item. Cancel closes the menu without applying anything. Then, we have small buttons with the green checkmark and red X, which will remove the limits and allow/disallow the item.

Clicking on an item with limits opens the menu up again, instead of requiring a right-click. It does not toggle allow/disallow by itself.

In vanilla, one can click-drag to change settings for multiple items; we respect vanilla click-drag to propagate allow and disallow, even over limits, and we should extend it so that click-drag also does that with limits (if the limits wouldn't match due to stack sizes, we apply only the right limit for that item).

We must be mindful of categories, and allow them to naturally propagate down; a player could set limits on Foods, or Meals below it, or individual Meals. If everything below it has the same limit, we can show that limit for the category. We can allow changing category limits to apply to everything below them as appropriate.

Vanilla has green check for allowed, yellow tilde for mixed, and red X for disallowed. To show mixed status with limits, we use a big I-beam style symbol: two large horizontal lines top and bottom, one central vertical line in the middle (this looks like a limiting symbol, and makes it clear to the player that mixed limits are in play).

We respect the vanilla "Clear all" and "allow all" buttons, which keep their vanilla behavior of allowing all items or disallowing all items (regardless of search).

### Batch Filling
Sometimes, players may not want colonists to fill stockpiles in ones and twos; this is particularly important if e.g. a stockpile is far away. Better to wait a bit and combine trips. Batch filling asks that the stockpile is only filled by a certain minimum amount, e.g. only bring at least 10 items, never less. Batch filling is independent of the stack limits. The batch filling button and entry lives under the stack limit apply and remove to search.

## Feeder stockpiles
It can be helpful to enforce a priority in stockpile flow. For example, we harvest crops outside, and collect them in an outdoor stockpile. Then we feed from there to the indoor freezer. For this, we enable stockpile flow patterns, "linking" them together (note: we must not confuse vanilla linking which keeps stockpile ). When selecting a stockpile, we have two new gizmos for it. One is "Set sources" and the other is "Set destinations." Any two stockpiles can be linked, and stockpiles can have multiple sources or destinations. The link may not be in play due to some settings, in which case we allow the link to show but be unfunctional.

We add a mod setting, "Autoset source stockpile priorities." Then, if setting another stockpile as a source, we take the destination's priority and set it one letter lower (see the subpriority stockpiles, below) for the source. Default on.

### Strict Limits or Not
When a stockpile has a source and/or destination, we enable two toggle buttons: "Only from source" and "Only to destinations", respectively. With these active, we only will enable hauling jobs to/from this stockpile from/to the linked sources or destinations, respectively. This ensures that a player controls the flow as they wish, and if they want to allow all incoming/outgoing hauling jobs if they wish.

We add another pair of toggles, for the only from source and only to destination settings, to default on or off for these settings when linking stockpiles. Default on for both.

### Linking patterns, circles, etc.
We absolutely do not need to enable any sort of circular flow restrictions if we use the restriction that all destination stockpiles must have a higher priority than the source stockpiles; then naturally we cannot form circles or have situations where pawns grab from the end of a chain to bring them to the 

### UI
We have four gizmos then: connect to a source, connect to a destination, show all connections, clear all connections. The first two are explained already, the show all connections just toggles the connections for all stockpiles on or off (only when a stockpile is selected), then the clear all connections will clear all links (to make this work, we must enforce a right-click and clear all, do not let a simple click of the gizmo clear all connections).

To show connections, we will refer to the Contagion mod as an example. Here, we draw bright cyan lines with arrows pointing in the correct direction near both the source and destination; we will use bright green arrows. This shows a source->destination path. If a link is not functioning for some reason (e.g. the destination has a lower priority than the source), we make it bright red instead and put small x marks along the line (still keeping the two arrows), making it clear it is not functioning.

## Subpriority stockpiles
Vanilla RimWorld gives us some control over stockpile priority with 5 levels, and with an optional tweak below we can expand that to 10. We'll want a subpriority system that appends a letter to the stockpiles to enable finer-tuning which stockpiles get materials first. Think a row of shelves; it may be useful to set those closest to the door as higher priority, so materials are readily available.

To set the subpriority, we will add a small box to the side of the "priority" (can we make the stockpile priority box slightly smaller?) with the letters (or nothing).

For this, we use an a-z system. No letter is the default, highest priority, then we sort within that priority from a-z, which is intuitive enough. The letters are purely within-priority, so 5z is still higher priority than 6.

We use a mod setting to let vanilla linking link subpriorities or not, default off. If it's on, the vanilla linking will set stockpiles to the same subpriority.

# Miscellaneous
Other items here:

## Optional Tweaks
These are small tweaks to stockpiles that we can enable/disable in mod settings, that affect how vanilla stockpiles work; players may not want them active.

### Stockpile priority numbers
Vanilla RimWorld uses a text-based priority system, e.g. low or critical priority. This doesn't affect how pawns perceive the importance of hauling jobs, but it's just an internal priority system for stockpiles. Instead, we can use a 1-10 number system, making the order more clear; we can also add a setting to make the priority go in reverse order (we'll use 1 as the default best, which matches vanilla's priority for work). Note, the setting to reverse it should be a UI change only; we keep the internal system as-is to avoid bugs. 

To keep consistency and enable swapping between settings, we set the vanilla stockpile priorities as follows internally using our numbers, with the number following in parentheses how we collapse back to vanilla from numbers:
Critical: 1 (take 1-2)
Important: 3 (take 3-4)
Preferred: 5 (take 5-6)
Normal: 7 (take 7-8)
Low: 10 (take 9-10)

## Miscellaneous Notes
Anything small and miscellaneous that we must also keep in mind

We should enable vanilla stockpile-setting copying and pasting, taking all of the PSC settings with it.

We respect vanilla's stockpile linking, which should preserve stockpile settings.