package loadout

// The Kick-Flight loadout, checked against the loader that will read it (SCRUM-580 / LO-1).
//
// These fixtures are the game repo's otomo/namespaces/kf.loadout/{schema.json,draft.json},
// copied here unchanged. The point is not that some JSON Schema library agrees with the
// file, but that ParseRules and Rules.Validate - the code that compiles a game's rules out
// of Config and decides whether a player's save may be stored - accept the document, refuse
// an id the game does not ship, and tolerate the "revision" member the store writes.

import (
	"encoding/json"
	"errors"
	"os"
	"reflect"
	"strings"
	"testing"
)

const (
	kfSchemaPath = "testdata/kf.loadout/schema.json"
	kfDraftPath  = "testdata/kf.loadout/draft.json"
	kfRulesPath  = "testdata/kf.loadout/session.loadout.json"
)

// kfRules parses the session.loadout document the game publishes - the namespace files
// wrapped by gen_schema.py --rules, copied here as the fixture - so what is tested is the
// artefact Config would serve, not a copy of it reassembled in Go. Neither optional member
// is set: Kick-Flight's loadout is a standing build, so it never expires, and its players
// may keep editing it while ready.
func kfRules(t *testing.T) *Rules {
	t.Helper()
	schema, err := os.ReadFile(kfSchemaPath)
	if err != nil {
		t.Fatalf("read the schema: %v", err)
	}
	raw, err := os.ReadFile(kfRulesPath)
	if err != nil {
		t.Fatalf("read the rules document: %v", err)
	}
	// The published rules document must carry this schema and not a hand-edited cousin.
	var published struct {
		Versions map[string]json.RawMessage `json:"versions"`
	}
	if err := json.Unmarshal(raw, &published); err != nil {
		t.Fatalf("the rules document is not JSON: %v", err)
	}
	var fromSchema, fromRules any
	if err := json.Unmarshal(schema, &fromSchema); err != nil {
		t.Fatalf("schema.json is not JSON: %v", err)
	}
	if err := json.Unmarshal(published.Versions["1"], &fromRules); err != nil {
		t.Fatalf("versions.1 is not JSON: %v", err)
	}
	if !reflect.DeepEqual(fromSchema, fromRules) {
		t.Fatal("session.loadout.json's versions.1 is not schema.json; rerun gen_schema.py --rules")
	}
	rules, err := ParseRules(raw)
	if err != nil {
		t.Fatalf("the game's rules document does not parse: %v", err)
	}
	if got := rules.Active(); got != 1 {
		t.Fatalf("active version = %d, want 1", got)
	}
	if got := rules.Lifetime(); got != 0 {
		t.Fatalf("lifetime = %v, want 0 (a loadout that never expires)", got)
	}
	if got := rules.ReadyPolicy(); got != ReadyFree {
		t.Fatalf("ready policy = %q, want %q", got, ReadyFree)
	}
	return rules
}

func kfDraft(t *testing.T) map[string]any {
	t.Helper()
	raw, err := os.ReadFile(kfDraftPath)
	if err != nil {
		t.Fatalf("read the draft: %v", err)
	}
	var doc map[string]any
	if err := json.Unmarshal(raw, &doc); err != nil {
		t.Fatalf("the draft is not a JSON object: %v", err)
	}
	return doc
}

func kfEncode(t *testing.T, doc map[string]any) []byte {
	t.Helper()
	raw, err := json.Marshal(doc)
	if err != nil {
		t.Fatalf("marshal the document: %v", err)
	}
	return raw
}

// kfIssues validates doc and returns "pointer: message" for every failure, so a test can
// name both the field and the reason.
func kfIssues(t *testing.T, rules *Rules, doc map[string]any) []string {
	t.Helper()
	err := rules.Validate(kfEncode(t, doc))
	if err == nil {
		return nil
	}
	var invalid *InvalidError
	if !errors.As(err, &invalid) {
		t.Fatalf("refused for a reason other than the document: %v", err)
	}
	out := make([]string, 0, len(invalid.Issues))
	for _, is := range invalid.Issues {
		where := is.Pointer
		if where == "" {
			where = "(document)"
		}
		out = append(out, where+": "+is.Message)
	}
	return out
}

// TestKFLoadoutDraftValidates is (a): the sample document the game publishes as its draft
// passes the loader, and so does every id the masters actually define.
func TestKFLoadoutDraftValidates(t *testing.T) {
	rules := kfRules(t)
	doc := kfDraft(t)
	if issues := kfIssues(t, rules, doc); issues != nil {
		t.Fatalf("the draft does not validate: %v", issues)
	}
	t.Logf("draft validates: schema version %d, decks %d, costumes %d",
		rules.Active(), len(doc["decks"].(map[string]any)), len(doc["costume_gears"].(map[string]any)))

	// The starter decks in masters_initial_disc_deck.json are in the schema's disc enum: the
	// generator reads the same master, so this pins that it really did.
	decks := doc["decks"].(map[string]any)
	for _, number := range []string{"1", "2", "3"} {
		deck, ok := decks[number].([]any)
		if !ok {
			t.Fatalf("the draft has no deck %s", number)
		}
		if len(deck) != 4 {
			t.Fatalf("deck %s holds %d discs, want the client's 4", number, len(deck))
		}
	}
}

// TestKFLoadoutRejectsUnknownIDs is (b): a disc or gear id the master does not define is
// refused by the schema itself, not by the adapter. The pointer names the slot, so the
// client can put the message next to the field (LO-5's issues[] shape).
func TestKFLoadoutRejectsUnknownIDs(t *testing.T) {
	rules := kfRules(t)

	cases := []struct {
		name   string
		mutate func(doc map[string]any)
		want   string
	}{
		{
			name: "a disc id no masters_disc.json row defines",
			mutate: func(doc map[string]any) {
				// 3010099 is inside the master's 3010001-3010138 range but is one of the
				// six gaps, which is why the schema enumerates ids instead of matching a
				// pattern.
				doc["decks"].(map[string]any)["1"].([]any)[0] = 3010099
			},
			want: "/decks/1/0",
		},
		{
			name: "a gear id no masters_gear.json row defines",
			mutate: func(doc map[string]any) {
				doc["costume_gears"].(map[string]any)["2010101"].([]any)[1] = 501
			},
			want: "/costume_gears/2010101/1",
		},
		{
			name: "a costume id no masters_kicker_costume.json row defines",
			mutate: func(doc map[string]any) {
				doc["costume_gears"].(map[string]any)["2999999"] = []any{0, 0, 0}
			},
			// propertyNames is checked on the map, and this library reports the failure at
			// the instance location of the map's parent (here the document root), so the
			// message names the enum and the pointer is coarse. The alternative - accepting
			// any key and trusting the adapter to resolve it - would put an unknown costume
			// in the stored document, so the coarse pointer is the better trade.
			want: "value must be one of '2010101'",
		},
		{
			name:   "a fourth deck, which the master does not define",
			mutate: func(doc map[string]any) { doc["decks"].(map[string]any)["4"] = []any{3010001, 3010002, 3010003, 3010004} },
			want:   "/decks",
		},
		{
			name:   "a deck of five discs",
			mutate: func(doc map[string]any) { doc["decks"].(map[string]any)["2"] = []any{3010005, 3010006, 3010007, 3010008, 3010009} },
			want:   "/decks/2",
		},
		{
			name:   "a costume with two gear slots instead of three",
			mutate: func(doc map[string]any) { doc["costume_gears"].(map[string]any)["2010201"] = []any{0, 0} },
			want:   "/costume_gears/2010201",
		},
		{
			name:   "an active deck the master does not define",
			mutate: func(doc map[string]any) { doc["active_deck"] = 4 },
			want:   "/active_deck",
		},
		{
			name:   "a document with no active deck",
			mutate: func(doc map[string]any) { delete(doc, "active_deck") },
			want:   "(document)",
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			doc := kfDraft(t)
			tc.mutate(doc)
			issues := kfIssues(t, rules, doc)
			if issues == nil {
				t.Fatalf("%s: validated, want a refusal", tc.name)
			}
			joined := strings.Join(issues, "; ")
			if !strings.Contains(joined, tc.want) {
				t.Fatalf("%s: issues %q do not name %s", tc.name, joined, tc.want)
			}
			t.Logf("refused at %s", joined)
		})
	}
}

// TestKFLoadoutAcceptsStoreRevision is (c): the store writes "revision" into the document it
// keeps (PutLoadout: jsonb_set(document,'{revision}',...)), so the document a GET returns is
// the saved one plus that member. Saving it back must still validate, which is what
// parseDocument's delete(obj,"revision") buys: a game's schema may close itself with
// additionalProperties:false and still round-trip a stored document.
func TestKFLoadoutAcceptsStoreRevision(t *testing.T) {
	rules := kfRules(t)

	stored := kfDraft(t)
	stored["revision"] = 7 // as the store writes it on the seventh save
	if issues := kfIssues(t, rules, stored); issues != nil {
		t.Fatalf("a document carrying the store's revision does not validate: %v", issues)
	}

	// A client that echoes its own revision back is not trusted either: validation never
	// sees the member, so a wrong one is not an error at this layer (the store replaces it).
	stored["revision"] = 1
	if issues := kfIssues(t, rules, stored); issues != nil {
		t.Fatalf("a document carrying a stale revision does not validate: %v", issues)
	}

	// And without the member at all, which is what a client submits for a first save.
	delete(stored, "revision")
	if issues := kfIssues(t, rules, stored); issues != nil {
		t.Fatalf("a document with no revision does not validate: %v", issues)
	}
	t.Logf("revision is accepted whether present, stale or absent")
}

// TestKFLoadoutVersionIsRefused keeps the versioning honest: the rules define version 1 only,
// and a document naming another version is refused as unknown rather than checked against
// the wrong schema.
func TestKFLoadoutVersionIsRefused(t *testing.T) {
	rules := kfRules(t)
	doc := kfDraft(t)
	doc["schema_version"] = 2
	err := rules.Validate(kfEncode(t, doc))
	var unknown *UnknownVersionError
	if !errors.As(err, &unknown) {
		t.Fatalf("validating a version 2 document: got %v, want *UnknownVersionError", err)
	}
	t.Logf("version 2 refused: %v", err)
}
