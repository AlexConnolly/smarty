using System.Text.Json.Nodes;

namespace Smarty.Brain;

/// <summary>
/// What the model is told when it turns a sentence into graph writes.
/// </summary>
/// <remarks>
/// The brief's job is to make reuse possible and duplication unattractive, and it does that by handing over everything
/// already on file rather than by asking for care. A model shown <c>majorca-trip | Majorca trip (project) — also: the
/// holiday</c> will point at it; a model shown nothing will invent a second one, and no amount of instruction fixes
/// that, because it is missing information rather than being careless.
/// </remarks>
public static class Reconcile
{
    public static string Brief(string statement, bool retract, string known, string? self, string kinds,
        string wording, DateTimeOffset today, string? file = null, string? says = null,
        IReadOnlyList<string>? refused = null, IReadOnlyList<string>? already = null) => retract
        ? "Something is no longer true, and your job is to say exactly which recorded connection to end.\n\n" +
          "Everything currently on file — the id, the name, the kind, and any other names it answers to:\n" +
          known + "\n" +
          "What you were told is no longer the case:\n" +
          $"  \"{statement}\"\n\n" +
          "Return the connections to end. Use the ids above. Only end things that are ACTUALLY THERE — if what was " +
          "said doesn't match anything on file, return nothing at all, because ending the wrong connection loses a " +
          "fact and invents a story about why.\n" +
          "Give a reason for each one, in the words used if a reason was given. The reason is the most valuable part: " +
          "later, a question about why something is no longer the case is answerable only if somebody wrote down why.\n" +
          "Nothing to end is a fine answer. Return an empty list."

        : "You keep a MAP OF SOMEBODY'S WORLD: who and what is in it, and how those are connected to each other. " +
          "Your job is to turn one sentence into the connections it states.\n" +
          // One question, replacing two lists of categories.
          //
          // The lists were: anything TIME-SPECIFIC, and anything OUTSIDE FORCES CAN CHANGE. The second was right. The
          // first said "a date" and so refused a birthday — which never stops being true and which nothing but this
          // person can tell you — and refused who is coming to a wedding, which is a social connection wearing an
          // occasion. It had already eaten one real fact ("my favourite day to relax is Sunday", recorded as nothing,
          // twice) and been patched with a special case rather than fixed. Category rules are a list of the last wrong
          // answer and the list has no end.
          //
          // What both were reaching for is one question, and it is answerable without judgement.
          "ONE QUESTION DECIDES EVERYTHING BELOW: COULD ANYBODY BUT THIS PERSON HAVE TOLD YOU? Who is who to them, a " +
          "birthday, what their brother likes, what they cannot eat, somewhere they love, something they decided, who " +
          "is coming to the wedding — none of that is written down anywhere else, and holding it is the whole reason " +
          "this exists.\n" +
          "WHAT CAN BE LOOKED UP IS OUT. Opening hours, a price, what is in stock, when the train goes, whether they " +
          "are free on Thursday, what is on this week: a search answers the first of those and their own calendar, " +
          "inbox and accounts answer the rest. A copy kept here goes quietly wrong while still reading as current, and " +
          "it answers a question somebody else could already answer.\n" +
          "THIS IS NOT A MAP OF THE WORLD. It is a map of ONE PERSON'S world, and it is worth having only where it " +
          "holds what no search can. Record the way BACK to what was looked up instead — the café, the account, the " +
          "identifier, the name a thing is filed under — so whatever does the looking knows where to go.\n\n" +
          "Everything currently on file — the id, the name, the kind, and any other names it answers to:\n" +
          known + "\n" +
          "What you have just been told:\n" +
          $"  \"{statement}\"\n\n" +
          "Return the facts it states, as a list. Each one is a FROM, a LABEL, and then either a TO (another thing) or " +
          "a VALUE (words) — and HOW LONG IT LASTS.\n\n" +
          // The one question that separates a map from a log, asked as a field rather than as advice. Everything that
          // has gone wrong here answers it honestly and is then dropped by the code: a tracker somebody asked for, a
          // panel that got published, a job in progress. Asking "is this relevant?" instead produced a new wrong answer
          // every time, because a request IS a true fact about them — it is just not a lasting one.
          "how long it lasts is one of these three, and it decides whether the fact is kept at all:\n" +
          $"    {Lifetimes.Forever} — it will not stop being true. Who somebody is to them, where they are from, " +
          "something they decided, somewhere they went.\n" +
          $"    {Lifetimes.WhileItHolds} — true now, and stays true until it changes. Where they live, what they " +
          "own, what they prefer, who they work for.\n" +
          $"    {Lifetimes.UntilItIsDone} — it ends when a piece of work ends. Something they have ASKED for, need, " +
          "want made, want fixed or want found out; a job under way; anything that exists because of the work rather " +
          "than in their world. THESE ARE NOT KEPT — the map is not what they are having done, it is what is there.\n" +
          "Answer it for what the fact IS, not for how interesting it seems. A request is a real fact and still ends " +
          "the moment it is served, so it is the third one.\n\n" +
          "Use a TO when the other end could hold connections of its own, and a VALUE when it only describes.\n\n" +
          "Naming things:\n" +
          "- If it is already on file above, use its id EXACTLY. This matters more than anything else here: a second " +
          "  node for something already recorded splits the memory in two, and every later question finds one half " +
          "  and misses the other.\n" +
          "- If it is genuinely new, write its name in full. It will be created.\n" +
          // The mechanism, offered where names are being decided. Without it a shortened name has nowhere to go, and the
          // only expressible answer is a second person — which is the exact split this section warns about.
          "- ANOTHER NAME for the same thing is a name, not a second thing. If the sentence gives one — a nickname, a " +
          "  shortening, a full name for somebody already known by less, what a place is called locally — declare the " +
          "  thing in things and list them in `also`. Then every later mention of either lands on the one thing.\n" +
          "- A THING ON FILE UNDER A DESCRIPTION, once you learn its name, is RENAMED rather than given another " +
          "  alias. Much of this gets created before anybody says a name: \"my wife\" is on file as \"Alex's wife\", " +
          "  \"the woman who runs the deli\" as itself. When the sentence says what they are actually called, put the " +
          "  thing in things with `name` as it stands on file and `called` as the real name. An alias would leave " +
          "  every answer still calling her Alex's wife. Nothing is lost — the description is kept as another name.\n" +
          "- Always say the KIND of each thing, even when using an id. The kind is the one thing a question is allowed " +
          "  to filter on, so anything filed under the wrong one cannot be found by asking for its sort.\n" +
          "  The kinds this memory already uses, with how many things are filed under each:\n" +
          kinds + "\n" +
          "  Reuse one of those. Only if NOTHING there fits, coin a new kind as a single lowercase word — and prefer " +
          "  reusing to coining, because two kinds meaning the same thing split the only filter that works. A kind says " +
          "  what something IS, never what is happening to it or what it is for.\n" +
          (self is { Length: > 0 }
              ? $"- \"me\", \"I\", \"my\" and \"we\" mean {self} — use that id. So does a sentence with NO subject at " +
                "all: \"loves the beach\", \"prefers the aisle seat\" are about them, because they are the one " +
                "talking.\n\n"
              // Said plainly rather than left implied. Told to use an id for a person who is not on file, a good model
              // records nothing at all — which is the right call, and looks exactly like a broken reconciler from here.
              : "- \"me\", \"I\" and \"my\" mean the person whose memory this is. Nobody has been recorded as them " +
                "yet, so write their name if the sentence gives one, and otherwise leave those facts out.\n\n") +
          "Labels are ordinary words naming the connection, not codes and not a description of the whole fact.\n" +
          "Labels already in use here, for reference:\n    " + wording + "\n" +
          // "Reuse one wherever it fits" was the instruction, and it fitted everything: one label got applied to a
          // brother, a neighbour and a reservoir, recording three things that are not true. Reuse is only ever right for
          // the SAME relationship — anything else is a wrong fact wearing a familiar word, which is worse than a new one.
          "Reuse one only when it means the same relationship as one of those. If none of them does, write the words " +
          "this sentence used instead — a label that is merely CLOSE is a false fact.\n" +
          // Said here because the graph ENFORCES it, and a writer who cannot see the rule proposes the same refused edge
          // every time — which is what happened: a sister survived as a question rather than a fact. Read off the same
          // list that does the refusing, so the two can never drift apart.
          $"A link to a PERSON is refused for these: {string.Join(", ", Graph.EmptyForPeople)}. Nobody HAS a person. " +
          "The sentence always says how two people are related, and that is the only part worth recording.\n\n" +
          "Record what the sentence MEANS, not only its surface. One sentence usually states several facts, and the one " +
          "most often missed is the relationship that made something worth mentioning at all: when a person or thing is " +
          "referred to by how it relates to somebody, that relationship is itself a fact and needs its own entry. Leave " +
          "it out and the thing is recorded but unreachable from the person it belongs to, which makes it unfindable by " +
          "anyone who asks in those terms.\n\n" +
          (file is { Length: > 0 }
              ? $"A DOCUMENT came with this, called \"{file}\".\n" +
                (says is { Length: > 0 }
                    ? $"Here is what it says:\n---\n{says}\n---\n\n" +
                      "Record what the DOCUMENT states as well as what the sentence states, and give the document the " +
                      "weight: the sentence is usually somebody handing it over, while the document is where the facts " +
                      "are. Everything it names that could hold connections of its own becomes a thing — the places, " +
                      "the people, the endeavour it is part of — joined up as the document describes, with its times " +
                      "and amounts and references as values. A thing named after the sentence alone, holding a file and " +
                      "connected to nothing, is the outcome to avoid: it can be handed back and never found.\n"
                    : "Nothing readable could be got out of it, so record only what the sentence says.\n") +
                "Say which thing the document itself belongs to in attachTo, using the same id or name you used above " +
                "— whatever it is a document OF.\n\n"
              : "") +
          // The durability test lives HERE, and putting it only where runs summarise themselves was the mistake. That
          // produces sentences, which then pass through this — so a total that slipped past the first filter got recorded
          // faithfully, and a reporting period became a thing. This is the one point every write goes through, whoever
          // wrote it, which is exactly the argument for resolving names here too.
          // One test instead of a list of forbidden categories, because the list was never the rule — this is. It settles
          // every case that has gone wrong here: a shop's opening hours and a sales total both move without the person
          // touching anything, while the details on a ticket they handed over are theirs to keep even though an airline
          // could change them tomorrow.
          "The two exclusions again, because this is where every mistake has been made:\n" +
          "COULD IT CHANGE WITHOUT THEM? Hours, an address, a price, what is available, a figure that moves when " +
          "somebody else acts — not theirs, not here. Record the way BACK to it: the account, the identifier, the name " +
          "a thing is filed under. What changes only when THEY change it belongs: who somebody is to them, something " +
          "they hold, something they decided, a preference.\n" +
          "IS IT TIED TO A TIME? Then it is not for here, whatever else is true about it. An appointment, a booking, a " +
          "flight, a gig, a delivery, what they did yesterday, what they are doing on Friday — all real, none of them " +
          "map. There is nowhere to put a date in what you return, and that is deliberate rather than an oversight: " +
          $"today is {today:dddd d MMMM yyyy} and the map would be wrong by Monday.\n" +
          "The line runs between the happening and what it tells you. \"Dinner at Fonda on Friday\" is a diary entry " +
          "and goes nowhere; \"Fonda is their favourite restaurant\" is a preference and belongs. If a sentence " +
          "contains both, keep the second and drop the first.\n\n" +
          (refused is { Count: > 0 }
              // Handed back rather than explained again. The rule was already stated above and was written straight
              // past; what changes an answer is the specific edge that was rejected and the reason it was.
              ? "One reading of this has already been tried and the graph REFUSED part of it:\n" +
                string.Concat(refused.Select(r => $"  {r}\n")) +
                (already is { Count: > 0 }
                    // Without this the second reading re-derives the whole sentence and writes all of it again. It did:
                    // handed one refusal, it produced a second edge saying what a first edge already said in different
                    // words, so correcting a label cost a duplicate fact.
                    ? "The rest of it is ALREADY RECORDED and must not be written again:\n" +
                      string.Concat(already.Select(a => $"  {a}\n"))
                    : "") +
                "Return ONLY the refused facts, written so they survive. The relationship is in the sentence — use " +
                "its words.\n\n"
              : "") +
          // Both halves of this were learned the same way, and they live in the ONE brief rather than in a second
          // attempt. A retry that says something different from the first attempt is not a retry — it is an admission
          // that the first prompt was wrong, and it hides the defect, because every sentence the brief handles badly
          // quietly gets a second bite and nobody ever fixes the brief.
          "A PREFERENCE IS A FACT, and the commonest one to miss: \"their favourite day to relax is Sunday\", \"they " +
          "like the beach early\". Nobody could have looked that up. The day or the time goes in a VALUE.\n\n" +
          "Only what was actually said. Do not add what you assume, do not fill in detail nobody gave, and do not " +
          "record the conversation itself. A sentence that states no facts returns an empty list, which is a correct " +
          "and useful answer — but read it once more before deciding that, because a preference stated in passing is " +
          "the easiest thing here to miss.";

    /// <summary>
    /// The plan's shape.
    /// </summary>
    /// <remarks>
    /// One string per node reference, deliberately: an id if it exists, a name if it does not. Asking the model to ALSO
    /// declare which of those it meant adds a field it can contradict itself on, and the code can tell perfectly well by
    /// trying to resolve it. Nothing required at the top level, so "this says nothing" is expressible.
    /// <para>
    /// The kinds are free strings rather than an enumeration, because the vocabulary is open — a closed enum forces
    /// everything unfamiliar into the catch-all kind, which is where a filter goes to die. They are still REQUIRED, and
    /// drift is held off by showing the kinds already in use rather than by forbidding new ones.
    /// </para>
    /// </remarks>
    public static JsonNode Schema { get; } = new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            // Which thing a document handed over with the sentence belongs to. Only the reading of the sentence knows.
            ["attachTo"] = new JsonObject { ["type"] = "string" },

            // Things declared in their own right: the other names they answer to, and — for the kinds that carry a
            // shape — when they happen. A thing needs an entry here only when there is something to say about it
            // beyond the facts joining it to everything else.
            //
            // Separate from the writes on purpose. A moment stated on a write belongs to that write — swam at the lido on
            // Saturday — while an appointment's time belongs to the appointment, and a write has two ends, so hanging it
            // off one of them means guessing which. Declared here it is unambiguous, and the writes go on referring to
            // things by name exactly as before.
            ["things"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["name"] = new JsonObject { ["type"] = "string" },
                        ["kind"] = new JsonObject { ["type"] = "string" },

                        // The other names it answers to — the mechanism that decides whether a shortened name lands on
                        // the person it belongs to or invents a second one. Nothing could state it before, so every
                        // nickname anybody used arrived as a stranger.
                        ["also"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" },
                        },

                        // What it is ACTUALLY called, when what is on file is a description.
                        //
                        // Half this graph gets created before anybody says a name: "my wife" lands as "Alex's wife",
                        // "the guy who fixed the boiler" lands as itself. Then the name arrives — and the only thing
                        // that could be expressed was another ALIAS, so the node stayed titled with the placeholder
                        // and answered to a real name nobody could see. Every reading of the graph, every panel and
                        // every answer went on calling her Alex's wife.
                        //
                        // Renaming is safe because the old name is kept as an alias: every phrase that used to find
                        // it still does. So this can never lose a way in, only add a better title.
                        ["called"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("name", "kind"),
                },
            },
            ["writes"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["from"] = new JsonObject { ["type"] = "string" },
                        ["fromKind"] = new JsonObject { ["type"] = "string" },
                        ["label"] = new JsonObject { ["type"] = "string" },
                        ["to"] = new JsonObject { ["type"] = "string" },
                        ["toKind"] = new JsonObject { ["type"] = "string" },
                        ["value"] = new JsonObject { ["type"] = "string" },
                        ["note"] = new JsonObject { ["type"] = "string" },

                        // How long the fact lasts — and so whether it is a memory at all.
                        //
                        // An enumeration on purpose, and required on purpose. The question it asks is factual, which is
                        // why it works where every previous attempt failed: those asked the writer to judge whether a
                        // fact was RELEVANT, and it always had a defensible answer, because a request somebody made is
                        // perfectly true and perfectly about them. It just stops being true when the job is done, and
                        // the code can act on that without judging anything.
                        ["lasts"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray(Lifetimes.Forever, Lifetimes.WhileItHolds, Lifetimes.UntilItIsDone),
                        },

                        // There is deliberately NOWHERE to put a date. A dated fact is a diary entry, the diary is the
                        // schedule and the dated list items, and a field for one here is an invitation to duplicate
                        // them — which is exactly what happened: appointments, bookings and "what is on this week"
                        // arrived as memories that were wrong by the following Monday.
                    },

                    // The kinds are REQUIRED, and the difference is not subtle. Left optional they were simply never
                    // supplied, so everything landed as a topic — and the kind is the one thing a question is allowed to
                    // filter on, so "who is coming on the trip" found nobody at all while the graph looked perfectly
                    // healthy. Emma has to be a person or she cannot be counted as one.
                    //
                    // Requiring it on an EXISTING reference costs nothing: anything already on file keeps the kind it
                    // has, and the stated one is ignored.
                    ["required"] = new JsonArray("from", "fromKind", "label", "lasts"),
                },
            },
        },
    };

    public static JsonNode RetractSchema { get; } = new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["retract"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["from"] = new JsonObject { ["type"] = "string" },
                        ["label"] = new JsonObject { ["type"] = "string" },
                        ["to"] = new JsonObject { ["type"] = "string" },
                        ["because"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("from", "label", "to", "because"),
                },
            },
        },
    };
}
