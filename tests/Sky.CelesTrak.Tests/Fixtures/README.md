# CelesTrak fixtures

`stations-2026-09-24.json` is a real response, byte for byte, from:

```text
GET https://celestrak.org/NORAD/elements/gp.php?GROUP=stations&FORMAT=json
Date: Thu, 24 Sep 2026 23:09:45 GMT
Content-Type: application/json; charset=UTF-8
Content-Length: 9290
```

The response carried no `Last-Modified`, `ETag`, or `Cache-Control` headers.

It holds 22 records, including two with 6-digit catalog numbers (100057 SOYUZ-MS 29
and 100712 PROGRESS-MS 35) that the TLE format cannot represent. `MEAN_MOTION_DDOT`
appears both as the integer `0` and as a decimal. SHA-256:
`215f70987951a7cb1fab2c82e74d60db83679a19abaa0628ba552454fc833e22`.
