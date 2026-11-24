# Status Board

A Windows dashboard showing the public status of the services a person depends on, plus their own website, ping and
LibreNMS checks.

## Language

### The board

**Status bar**:
The card at the top of the board: headline, refresh line, counters, Needs attention chips and the news ticker.
_Avoid_: Summary, header

**News ticker**:
The LIVE strip at the bottom of the status bar that scrolls the latest update of each active incident.
_Avoid_: News scroller, marquee, crawl

**Chip**:
A compact, clickable pill in the Needs attention row naming one service that isn't operational.
_Avoid_: Tag, badge, pill

**Tile**:
One service's block on the board, showing its status, incident stage and what it affects.
_Avoid_: Card, box

**Clock**:
Today's time and date, centred at the top of the window.
_Avoid_: Date and time, timestamp

**Refresh line**:
The "Updated … · next in …" line under the status bar's headline; distinct from the clock.
_Avoid_: Last updated, timestamp
