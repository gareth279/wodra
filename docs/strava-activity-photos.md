# Strava activity screenshot attachments

Checked against Strava's official API reference and upload documentation on 15 October 2026.

The documented public API can create activities from a data file with `POST /uploads`, create manual activities with `POST /activities`, and read an activity's fields. The activity model exposes photo counts and a cover-photo URL, but the API reference does not document an operation for uploading or attaching photos to an activity. The upload API accepts FIT, TCX, or GPX data files; it is not a photo upload facility.

The documented `activity:write` scope allows manual activity creation and data-file uploads. It does not grant photo attachment access. The public documentation does not describe a Standard, subscription, or partner access tier that enables a photo attachment endpoint. Because no such endpoint is part of the documented public API, Wodra cannot verify or use app eligibility for it. This conclusion does not rule out an undocumented private or separately negotiated Strava capability; Wodra will not call private endpoints or scrape the website.

Consequently, Wodra does not attach ride screenshots to Strava activities. Issue #36 remains open until Strava documents a supported endpoint and confirms that Wodra's application is eligible to use it. This does not affect FIT uploads.

Sources:

- [Strava API reference: activities and uploads](https://developers.strava.com/docs/reference/)
- [Strava activity uploads](https://developers.strava.com/docs/uploads/)
- [Strava OAuth scopes](https://developers.strava.com/docs/authentication/)
