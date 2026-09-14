## What happens

`DirectDial.ResolveStationEndpointAsync` ([`src/Macula/Dht/DirectDial.cs:237-279`](https://github.com/macula-io/macula-dotnet/blob/3d56842b5b436be4136eb33f2cc9dc6113cf2d86/src/Macula/Dht/DirectDial.cs#L237-L279)) throws `StationEndpointNotFoundException` in three different situations:

- no `station_endpoint` record is found on any retry ([247-251](https://github.com/macula-io/macula-dotnet/blob/3d56842b5b436be4136eb33f2cc9dc6113cf2d86/src/Macula/Dht/DirectDial.cs#L247-L251), then [278](https://github.com/macula-io/macula-dotnet/blob/3d56842b5b436be4136eb33f2cc9dc6113cf2d86/src/Macula/Dht/DirectDial.cs#L278));
- every record returned is expired ([261-266](https://github.com/macula-io/macula-dotnet/blob/3d56842b5b436be4136eb33f2cc9dc6113cf2d86/src/Macula/Dht/DirectDial.cs#L261-L266), then [278](https://github.com/macula-io/macula-dotnet/blob/3d56842b5b436be4136eb33f2cc9dc6113cf2d86/src/Macula/Dht/DirectDial.cs#L278));
- the record has an empty `host_advertised` list ([272-275](https://github.com/macula-io/macula-dotnet/blob/3d56842b5b436be4136eb33f2cc9dc6113cf2d86/src/Macula/Dht/DirectDial.cs#L272-L275)).

The exception ([`DirectDial.cs:51-54`](https://github.com/macula-io/macula-dotnet/blob/3d56842b5b436be4136eb33f2cc9dc6113cf2d86/src/Macula/Dht/DirectDial.cs#L51-L54)) always carries the same message, "directdial: resolved station published no reachable station_endpoint", and exposes neither the station's node id nor which of the three situations occurred.

It reaches callers of every direct-dial path that resolves a station: `ResolveAsync` ([89](https://github.com/macula-io/macula-dotnet/blob/3d56842b5b436be4136eb33f2cc9dc6113cf2d86/src/Macula/Dht/DirectDial.cs#L89)), `ResolveWithCertChainAsync` ([161](https://github.com/macula-io/macula-dotnet/blob/3d56842b5b436be4136eb33f2cc9dc6113cf2d86/src/Macula/Dht/DirectDial.cs#L161)) and `PutDirectAsync` ([504](https://github.com/macula-io/macula-dotnet/blob/3d56842b5b436be4136eb33f2cc9dc6113cf2d86/src/Macula/Dht/DirectDial.cs#L504)), and through them `CallAsync`, `CallWithUcanAsync`, `CallWithCertChainAsync`, `OpenStreamDirectAsync` and `OpenStreamDirectWithCertChainAsync`, as well as `ResolveStationEndpointAsync` itself.

## Expected

From the exception, a caller can tell which station's endpoint could not be resolved, and whether the record was absent, expired, or had no advertised host.

## How it was found

A canary run of macula-dotnet 0.4.0 (3d56842) on 2026-09-11 against a live server-stream procedure on the demo fleet, resolved by direct dial, plus reading the code above. The direct dial failed with this exception in two runs. Identifying the cause took separate DHT lookups, which showed that the serving station's only visible `station_endpoint` record had expired.

