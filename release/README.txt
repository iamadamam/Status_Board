Status Board 1.0
================

A small dashboard for Windows that shows the public status of the cloud, SaaS and everyday
services you rely on (Microsoft 365, Azure, AWS, Google, Slack, Zoom, Salesforce, Stripe and
more than 180 others) in one window. It can also watch your own websites and hosts.

Status comes only from each vendor's public status page or feed. No accounts, no sign-in, no
telemetry, and no data about you is sent anywhere.


GETTING STARTED
---------------
1. Unzip this folder anywhere (your Documents, a shared drive or a USB stick).
2. Run StatusBoard.exe. No installation or .NET download is needed.
3. A short setup asks for a profile name, the vendors you rely on and a theme.

Windows may show a SmartScreen warning the first time, because the app is new and unsigned:
choose "More info", then "Run anyway".

Requires Windows 10 or 11 (64-bit). No administrator rights needed.


USING IT
--------
- Problems are shown first. Click a tile, a "Needs attention" chip or a ticker item to see the
  incidents, affected components and a link to the official status page.
- Vendors (top right): choose what to watch, narrow a vendor to the products or components you
  use, arrange vendors into groups, and add any other status page or feed, a website check, a
  ping check or LibreNMS.
- Settings (gear icon): profiles, refresh interval, theme, tray icon, notifications, the
  scrolling news ticker, and your own vendor logos.
- Profiles: each profile is its own board. Switch from the top-left corner.
- TV mode (F11): full screen for a wall screen, slowly scrolling. Esc leaves.
  Start straight into it with:  StatusBoard.exe --tv
- Resize tiles with - / + in the header, Ctrl + mouse wheel, or Ctrl + / Ctrl - / Ctrl 0.
- F5 refreshes now. Esc closes the details panel.


YOUR SETTINGS
-------------
Everything is kept next to StatusBoard.exe:
  profiles.json         your list of profiles
  statusboard.json      the first profile's settings (others: profile-*.json)
  logos\                cached vendor logos and any you upload

Nothing is written to AppData, the registry or anywhere else. To uninstall, delete the folder.
To move to another PC, copy the folder. Any LibreNMS API token is encrypted for your Windows
account, so re-enter it on the new PC.


NETWORK
-------
Status Board only makes outbound HTTPS requests to the status pages you choose (every 5
minutes by default) and, once, to vendor websites for their logos, plus any website or ping
checks you add yourself (every minute). It opens no ports and accepts no connections. It uses
your Windows proxy settings.


LICENCE
-------
Copyright (c) 2026 iamadamam. Licensed under the PolyForm Strict License 1.0.0
(see LICENSE.txt): free for noncommercial and personal use. Redistribution, modification
and commercial use are not permitted without separate permission.
Vendor names and logos belong to their owners; status data comes from their public pages.
