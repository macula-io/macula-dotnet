// Command capture serves one call on a station and writes the payload it
// received, as its provider received it off the wire, for a verifier in
// another stack. It is how a binding shows that a payload it signed survives
// the real path (its JSON, libmacula's CBOR, a signed CALL, a
// station, a provider's verification) into mcl_om's ownership proof verifier
// (macula-go's scripts/interop/erlang_ownership_proof.escript verify).
//
//	capture -station host:port@<node_id hex> -profile pq_hybrid -out <file> -realm <hex> -procedure <name>
//
// It serves ~<its node_id>/capture, prints its node_id on the first line of
// stdout, and writes <out>: the payload's CBOR hex, then -realm and
// -procedure, one per line: the realm and procedure the payload's proof
// names, which the verifier checks it against.
package main

import (
	"context"
	"encoding/hex"
	"flag"
	"fmt"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/macula-io/macula-go/cbor"
	"github.com/macula-io/macula-go/identity"
	"github.com/macula-io/macula-go/pool"
	"github.com/macula-io/macula-go/profile"
	"github.com/macula-io/macula-go/stationlink"
)

func main() {
	station := flag.String("station", "", "host:port@<node_id hex>")
	profileName := flag.String("profile", "pq_hybrid", "crypto profile")
	out := flag.String("out", "", "file to write")
	realm := flag.String("realm", "", "the realm the proof names, hex")
	procedure := flag.String("procedure", "", "the procedure the proof names")
	wait := flag.Duration("wait", time.Minute, "how long to wait for the call")
	flag.Parse()
	if err := run(*station, *profileName, *out, *realm, *procedure, *wait); err != nil {
		fmt.Fprintln(os.Stderr, "capture:", err)
		os.Exit(1)
	}
}

func run(station, profileName, out, realmHex, procedure string, wait time.Duration) error {
	p, err := profile.Parse(profileName)
	if err != nil {
		return err
	}
	seed, err := parseSeed(station)
	if err != nil {
		return err
	}
	key, err := identity.GenerateIdentityKey(p, identity.PuzzleDifficulty)
	if err != nil {
		return err
	}
	ctx, cancel := context.WithTimeout(context.Background(), wait)
	defer cancel()
	node, err := pool.Connect(ctx, []pool.Seed{seed}, pool.Opts{IdentityKey: key})
	if err != nil {
		return fmt.Errorf("connect: %w", err)
	}
	defer node.Close()
	self := node.NodeID()
	captured := make(chan cbor.Value, 1)
	served, err := node.Serve(ctx, pool.Offer{Realm: [32]byte{}, Procedure: "~" + hex.EncodeToString(self[:]) + "/capture",
		Handler: func(_ context.Context, r stationlink.Request) (cbor.Value, error) {
			select {
			case captured <- r.Payload:
			default:
			}
			return cbor.Text("captured"), nil
		}})
	if err != nil {
		return fmt.Errorf("serve: %w", err)
	}
	fmt.Println(hex.EncodeToString(self[:]))
	select {
	case payload := <-captured:
		// Let the reply go out before the pool closes.
		time.Sleep(200 * time.Millisecond)
		_ = served.Stop()
		body := hex.EncodeToString(cbor.Encode(payload)) + "\n" + realmHex + "\n" + procedure + "\n"
		return os.WriteFile(out, []byte(body), 0o600)
	case <-ctx.Done():
		return fmt.Errorf("no call within %s", wait)
	}
}

func parseSeed(text string) (pool.Seed, error) {
	address, node, ok := strings.Cut(text, "@")
	host, portText, found := strings.Cut(address, ":")
	raw, err := hex.DecodeString(node)
	port, perr := strconv.ParseUint(portText, 10, 16)
	if !ok || !found || err != nil || len(raw) != 32 || perr != nil {
		return pool.Seed{}, fmt.Errorf("-station is host:port@<node_id hex>, not %q", text)
	}
	return pool.Seed{Host: host, Port: uint16(port), NodeID: [32]byte(raw)}, nil
}
