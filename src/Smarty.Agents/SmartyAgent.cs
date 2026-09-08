using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Smarty.Agents;

/// <summary>
/// A smart agent: takes an <see cref="AgentInput"/> (conversation + tools + system prompt +
/// model spec) and produces a textual answer, calling tools as needed along the way.
///
/// <code>
/// // Buffered:
/// string answer = await new SmartyAgent(input).Answer("What is the current system status?");
///
/// // Streamed (output only, no chain-of-thought):
/// await foreach (var ev in new SmartyAgent(input).AnswerStream("..."))
///     if (ev is AgentEvent.ContentDelta d) Console.Write(d.Text);
/// </code>
/// </summary>
public sealed class SmartyAgent
{
    private readonly AgentInput _input;
    private readonly IModelProvider _provider;
    private readonly IModelProvider _secondaryProvider;
    private readonly Dictionary<string, AgentTool> _tools;

    // How many turns from the cap to start announcing the remaining budget (when AnnounceBudget is on).
    private const int BudgetWarnThreshold = 3;

    // How many times a repeatable tool may be called with identical arguments before it is treated as a loop.
    // Three allows the legitimate pattern — read, navigate, read again, navigate, read — and stops the pattern
    // that has no reading of the world behind it.
    private const int RepeatableCallLimit = 3;

    /// <summary>
    /// How many times a call may come back with the identical result before it's treated as a dead end. Two
    /// unchanged reads is a coincidence worth one more look; three is a worker grinding its budget away against a
    /// page that isn't moving.
    /// </summary>
    private const int UnchangedResultLimit = 3;

    /// <summary>
    /// How many times a tool may REFUSE in exactly the same words before the run is told to stop trying.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gap the other two guards left between them. One counts identical ARGUMENTS, and a worker trying to satisfy
    /// a complaint changes its arguments every time, so the count never rises. The other compares results and only
    /// looks at successful ones. An identical refusal, over and over, was invisible to both — and it is the clearest
    /// evidence of a loop there is, because the tool is saying the same sentence about the same objection.
    /// </para>
    /// <para>
    /// Found watching a panel sit on "Building the data feed" for four minutes: publishing was refused twelve times
    /// running, with the identical message, while the worker rearranged everything except the thing being objected to.
    /// Nothing in the run reported a fault; from outside it looked like slow work.
    /// </para>
    /// </remarks>
    private const int IdenticalRefusalLimit = 3;

    // A refusal this loop makes ITSELF must not count its own repeats out loud.
    //
    // The guard further down notices a tool refusing with the same objection over and over, and it compares the
    // sentences. The refusals below are the ones most certain to repeat — and they used to number themselves
    // ("…with these exact arguments 3 times", then 4, then 5), so every restatement was a different string and
    // reset the count. The escalation was unreachable against exactly the refusals it was written for: a panel
    // build looking for a football fixture was refused thirty-six times in a row by one of these sentences, each
    // with a larger number in it, and nothing ever concluded it was one objection.
    //
    // The count is not lost — the escalation itself states it, which is the place it means something.

    /// <summary>Sampling temperature for the one turn following a collapse. Enough noise to take a different
    /// path out of the state that degenerated, low enough that tool-calling stays reliable — the retry still
    /// has a job to do, and doing it accurately matters more than doing it differently.</summary>
    private const double LoopRetryTemperature = 0.3;

    public SmartyAgent(AgentInput input, ModelProviderRegistry? registry = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        var reg = registry ?? ModelProviderRegistry.Default;
        _provider = reg.Resolve(input.Model);
        _secondaryProvider = reg.Resolve(input.SecondaryModel ?? input.Model);
        _tools = input.Tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The transcript (including tool calls/results) of the most recent run.</summary>
    public AgentRun? LastRun { get; private set; }

    /// <summary>Ask the agent a question and stream the answer as it is written.</summary>
    public async IAsyncEnumerable<AgentEvent> AnswerStream(
        string userMessage, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (userMessage is null) throw new ArgumentNullException(nameof(userMessage));

        var conversation = _input.Conversation;
        conversation.Add(Message.User(userMessage));

        var run = new AgentRun();
        LastRun = run;

        int toolFailures = 0; // cumulative failed tool calls this run — drives the failure budget
        var toolCallCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // per-tool call budget
        var successfulToolCalls = new HashSet<string>(); // exact (name+args) signatures that succeeded
        // How many times each exact signature has been attempted, across the whole run — the loop guard for tools
        // that are otherwise allowed to repeat themselves.
        var repeatSignatureCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        // The last thing each call returned, so a result that hasn't moved can be recognised as one.
        var lastOutputBySignature = new Dictionary<string, string>(StringComparer.Ordinal);
        var unchangedResultCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        // Keyed by tool NAME, not by call signature: the whole point is that the arguments keep changing while the
        // objection does not.
        var lastRefusalByTool = new Dictionary<string, string>(StringComparer.Ordinal);
        var identicalRefusalCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        // The last tool run, so a repeat can be told from a return visit: only consecutive identical calls count.
        string? lastToolCalled = null;
        // When the next stand-back is due, in tool calls. Seeded so the first one lands after a real attempt
        // rather than immediately, and moved forward each time so a long run is paced rather than nagged.
        int nextReflectionAt = _input.ReflectEvery;
        // True for exactly the turn that follows a stand-back, so a plan stated in prose isn't mistaken for a
        // finished job.
        bool justReflected = false;

        int currentMaxIterations = _input.MaxIterations;
        int currentMaxCallsPerTool = _input.MaxCallsPerTool;
        int currentMaxToolFailures = _input.MaxToolFailures;
        bool isDeadEndWrapUp = false;

        // Non-null for ONE turn after a collapse: the retry samples with a little noise so it can't reproduce
        // the same degenerate output token for token. Cleared as soon as a turn comes back clean.
        double? retryTemperature = null;

        for (int iteration = 0; iteration < currentMaxIterations; iteration++)
        {
            var seenToolCallsInThisTurn = new HashSet<string>();
            // Pull in any out-of-band messages a caller queued while we were working, so a long task
            // can be steered or interrupted between turns rather than only before it starts.
            if (_input.DrainInbox is { } drain)
            {
                foreach (var injected in drain())
                {
                    conversation.Add(injected);
                    run.Messages.Add(injected);
                }
            }

            // A prior leg can be interrupted (cancel/timeout/watchdog abort) mid tool-call group, after the
            // assistant message with N tool calls was appended but before every call's result was. The
            // transcript is persisted and reused, so resending it would carry an assistant tool-call with no
            // matching tool result — which providers reject ("incomplete parallel tool-call group"). Close any
            // such gap before the model call so the group is always well-formed.
            EnsureToolCallsAnswered(conversation);

            // Stand back every so often and say what this is for.
            //
            // Every other guard here asks "was that call wasteful?" and none asks "is this run getting anywhere?" —
            // which is why a worker once alternated read_page and inspect two hundred times, each one refused by its
            // own per-tool cap, each refusal simply redirecting it to the other tool. The caps fired correctly, the
            // supervisor kept extending because every call returned something, and nothing ever asked it what it was
            // trying to establish.
            //
            // This lands in the worker's OWN transcript rather than being a verdict passed over its head, so it
            // changes the next decision instead of grading the last one. The important part is the prediction: an
            // expectation stated out loud is falsifiable three calls later, and a model reading its own failed
            // prediction is far likelier to change tack than one told to stop.
            int callsSoFar = run.ToolInvocations.Count;
            if (_input.ReflectEvery > 0 && callsSoFar > 0 && callsSoFar >= nextReflectionAt)
            {
                nextReflectionAt = callsSoFar + _input.ReflectEvery;
                var reflection = Message.System(
                    $"Pause. You have made {callsSoFar} tool calls. Before the next one, answer briefly — three or " +
                    "four sentences, no lists:\n" +
                    "1. What exactly are you trying to establish right now, and for which part of the task?\n" +
                    "2. What do you expect the next few calls to give you? Be specific enough that you will know " +
                    "if it doesn't happen.\n" +
                    "3. What has not been working, and what should you stop doing? If a tool has told you to stop, " +
                    "or the same approach has failed more than twice, that approach is finished — say what you will " +
                    "do instead, or say you have enough and conclude.\n" +
                    "This is for you, not the user. Then carry on.");
                conversation.Add(reflection);
                run.Messages.Add(reflection);
                justReflected = true;
            }

            // Budget awareness: as the turn cap approaches, tell the worker exactly how many turns remain so it
            // finishes the job — produces and SAVES its deliverable, then answers — instead of running out one
            // step short (a research step once made charts and ran out before writing its analysis file). Re-sent
            // each turn in the final stretch so the count stays accurate even if the cap was dynamically extended.
            int callsLeft = currentMaxIterations - iteration;
            if (_input.AnnounceBudget && callsLeft <= BudgetWarnThreshold)
            {
                var budgetNote = Message.System(
                    $"You have {callsLeft} tool-call turn{(callsLeft == 1 ? "" : "s")} left before you must stop. " +
                    "If you still owe a deliverable — a written result, or a file to save with write_file — produce " +
                    "and save it NOW, then give your final answer. Don't begin anything you can't finish in that budget.");
                conversation.Add(budgetNote);
                run.Messages.Add(budgetNote);
            }

            var request = new ModelRequest
            {
                Model = _input.Model.Model,
                SystemPrompt = _input.SystemPrompt,
                Messages = conversation,
                Tools = _input.Tools,
                MaxOutputTokens = _input.MaxOutputTokensPerTurn,
                RepeatPenalty = _input.RepeatPenalty,
                Temperature = retryTemperature,
                TurnTimeout = _input.TurnTimeout,
                Think = _input.Think,
            };

            ModelResponse? final = null;
            bool streamedContent = false;
            await foreach (var ev in _provider.StreamAsync(request, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                switch (ev)
                {
                    case ModelStreamEvent.Content content:
                        streamedContent = true;
                        yield return new AgentEvent.ContentDelta(content.Text);
                        break;
                    case ModelStreamEvent.Reasoning reasoning:
                        yield return new AgentEvent.ReasoningDelta(reasoning.Text);
                        break;
                    case ModelStreamEvent.Completed completed:
                        final = completed.Response;
                        run.Spend.Record(_input.Model.Model, final);
                        break;
                }
            }

            final ??= new ModelResponse();

            // Loop/timeout recovery: if the model got stuck (degenerate repetition or ran past the time
            // limit) and produced no tool call, don't treat the runaway turn as the answer. Discard it,
            // nudge the model to conclude using what it already has, and try again. Everything before
            // this turn (prior tool calls/results) stays in the conversation.
            //
            // A turn that produced NO answer and NO tool call is a dead end — it looped, timed out, hit the
            // token cap, or finished with its answer trapped in the thinking channel (no real content).
            //
            // But emptiness was the ONLY test, and a collapsed turn is usually not empty. The provider watches
            // the stream, spots the degenerate repetition and cuts it — leaving a coherent opening welded to a
            // wall of one repeated token: "Let me find the Waitrose link on the feedcrimson crimson crimson…".
            // That has content, so it read as a perfectly good answer, went into the transcript, and became part
            // of the context for the next turn — which made the next collapse likelier still. One shopping run
            // did that nineteen times and spent its whole budget on it. The stream already KNEW: FinishReason
            // .Loop was set and thrown away. Believe it.
            bool noAnswer = string.IsNullOrWhiteSpace(final.Content);
            bool collapsed = final.Finish == FinishReason.Loop;

            // The case that used to escape: a collapse that left REAL TEXT behind. An empty looping turn was
            // already caught by noAnswer and is left exactly as it was — it needs telling to go and answer,
            // which is a different instruction from "that sentence is gibberish, don't finish it".
            bool collapsedMidSentence = collapsed && !noAnswer;

            if ((noAnswer || collapsedMidSentence) && !final.HasToolCalls && _input.RecoverFromLoops
                && iteration < currentMaxIterations - 1)
            {
                // Drop the fragment on the floor. It is not evidence of anything, and the one thing that
                // reliably provokes the next collapse is the last one sitting in the context.
                if (collapsedMidSentence && streamedContent)
                    yield return new AgentEvent.ContentCleared();

                // At temperature 0 the retry would reproduce the collapse token for token — true whether the
                // loop happened in the answer or in the reasoning. The nudge changes the context, which helps,
                // but the sampling has to change too or "try again" means "do the identical thing again".
                if (collapsed) retryTemperature = LoopRetryTemperature;

                var recovery = Message.System(collapsedMidSentence ? _input.LoopCollapseNudge : _input.LoopRecoveryNudge);
                conversation.Add(recovery);
                run.Messages.Add(recovery);
                continue;
            }

            // Survived a clean turn — back to deterministic sampling for the next one.
            if (!collapsed) retryTemperature = null;

            // Some models (notably small qwen) sometimes "chat" the tool call — emitting it as JSON
            // text in the content instead of as a structured call. Recover it so we actually run the
            // command rather than printing it back to the user.
            IReadOnlyList<ToolCall> toolCalls = final.ToolCalls;
            string answerText = final.Content ?? "";

            // A collapse that still produced a real tool call: the intent survived, only the narration around
            // it degenerated. Keep the call — it's the thing that makes progress — and bin the words, which are
            // gibberish that would otherwise be transcribed and read straight back next turn.
            if (collapsed && toolCalls.Count > 0 && !string.IsNullOrWhiteSpace(answerText))
            {
                if (streamedContent) yield return new AgentEvent.ContentCleared();
                answerText = "";
            }
            if (toolCalls.Count == 0 && TryExtractInlineToolCalls(answerText, out var inlineCalls, out var cleaned))
            {
                toolCalls = inlineCalls;
                answerText = cleaned;
            }

            var assistantMessage = Message.Assistant(
                answerText, final.Reasoning, toolCalls.Count > 0 ? toolCalls : null);
            conversation.Add(assistantMessage);
            run.Messages.Add(assistantMessage);

            // A stand-back is not an answer.
            //
            // Asked to explain itself, the worker replies in prose and calls no tool — which is byte-for-byte the
            // shape of a finished job. So the run ended on the reflection and the user was handed "I'm gathering
            // photos of each resort, three to go… nothing has failed" as their brochure, including the line saying
            // it was not for them. The turn after a reflection is the one turn where text without a tool call means
            // "still working", so it is kept in the transcript and the loop carries on.
            if (justReflected && toolCalls.Count == 0)
            {
                justReflected = false;
                var resume = Message.System(
                    "Good. That was for your own benefit and the user has not seen it. Now DO the next thing you " +
                    "just described — call the tool. Do not summarise your plan again.");
                conversation.Add(resume);
                run.Messages.Add(resume);
                continue;
            }
            justReflected = false;

            if (toolCalls.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(answerText))
                {
                    yield return new AgentEvent.Completed(answerText);
                    yield break;
                }
                // No tool call AND no answer text (the model reasoned but said nothing, on the final
                // iteration where loop-recovery no longer kicks in). Don't return an empty result — fall
                // through to the graceful "couldn't finish" message so the agent admits defeat rather than
                // handing the caller a blank to invent around.
                break;
            }

            // This turn is a tool-call turn — discard any answer text we optimistically streamed
            // (it was the inline tool call / preamble, not the final answer).
            if (streamedContent)
                yield return new AgentEvent.ContentCleared();

            bool anySuccess = false, anyRetryable = false, anyTerminal = false;
            foreach (var call in toolCalls)
            {
                yield return new AgentEvent.ToolStarted(call.Name, call.Arguments.ToString());

                // Structural blocks — BEFORE running the tool — so the model can't burn the run repeating a
                // call or hammering one tool forever. These catch the failure mode where every call "succeeds"
                // with useless results (so the failure budget never trips): a relentless, going-nowhere loop.
                string signature = call.Name + "|" + call.Arguments.ToString().ToLowerInvariant();
                int priorCalls = toolCallCounts.TryGetValue(call.Name, out var cc) ? cc : 0;

                // A stateful tool is largely exempt from the repeat guards: identical arguments genuinely mean
                // something different once the world has moved. Reading a browser page after navigating elsewhere
                // is the case that matters — same {tabId}, different page — and refusing it blocks precisely what
                // the tool's description tells the model to do.
                //
                // Largely, not entirely. A blanket exemption removed the only thing standing between a stateful
                // tool and a loop: a run asked for photos took the same screenshot of the same page twice in a
                // row, then again, because nothing said no and a screenshot tells the model nothing it can read.
                // So repeats are allowed but counted, and the same call with the same arguments stops being
                // answered once it has clearly stopped being a fresh look at a changed world.
                //
                // "In a row" is the whole of it, and counting without that qualifier broke browsing outright: a
                // worker clicked a link, asked to read the page it had just opened, and was refused because
                // chrome_read_page{} is byte-identical every time it is called. Anything happening in between —
                // a click, a navigation, a scroll — means the world may have moved, so the count starts again.
                // Three reads with nothing between them is a loop; read, click, read, click, read is browsing.
                bool repeatable = _tools.TryGetValue(call.Name, out var declared) && declared.Repeatable;
                if (lastToolCalled is not null && !string.Equals(lastToolCalled, call.Name, StringComparison.Ordinal))
                    repeatSignatureCounts.Clear();
                lastToolCalled = call.Name;

                repeatSignatureCounts.TryGetValue(signature, out int priorIdentical);
                repeatSignatureCounts[signature] = priorIdentical + 1;
                bool repeatedTooOften = repeatable && priorIdentical >= RepeatableCallLimit;
                bool repeatInTurn = !seenToolCallsInThisTurn.Add(signature);

                ToolOutput output;
                if (repeatedTooOften)
                {
                    output = ToolOutput.DeadEnd(
                        $"You have already run {call.Name} with these exact arguments, more than once, and it " +
                        "has not changed. Whatever you are hoping will be different is not going to be. Use what " +
                        "you have, try a genuinely different approach, or say plainly that you couldn't get it.");
                }
                else if (repeatInTurn && !repeatable)
                {
                    // Exact repeat in the same turn
                    output = ToolOutput.DeadEnd(
                        $"You already ran {call.Name} with these exact arguments in this turn. Running it " +
                        "again changes nothing.");
                }
                else if (!repeatable && successfulToolCalls.Contains(signature))
                {
                    // Exact repeat of a successful call from a prior turn
                    output = ToolOutput.DeadEnd(
                        $"You already successfully ran {call.Name} with these exact arguments — its result is above. Running it " +
                        "again changes nothing. Use what you already have, take a genuinely different approach, or " +
                        "stop and give your best answer (or say plainly you couldn't get it).");
                }
                else if (priorCalls >= currentMaxCallsPerTool)
                {
                    if (_input.EnableSupervisorGuard)
                    {
                        var decision = await RunSupervisorGuardAsync(run, ct).ConfigureAwait(false);
                        if (decision == SupervisorDecision.Progress)
                        {
                            currentMaxCallsPerTool += Math.Max(2, _input.MaxCallsPerTool / 2);
                            currentMaxToolFailures += Math.Max(2, _input.MaxToolFailures / 2);
                            if (currentMaxIterations - iteration <= 2)
                            {
                                currentMaxIterations += Math.Max(4, _input.MaxIterations / 2);
                                var turnNudge = Message.System(_input.SupervisorProgressNudge);
                                conversation.Add(turnNudge);
                                run.Messages.Add(turnNudge);
                            }

                            toolCallCounts[call.Name] = priorCalls + 1;
                            output = await ExecuteToolAsync(call, ct).ConfigureAwait(false);
                            if (!output.IsError)
                            {
                                successfulToolCalls.Add(signature);
                            }
                        }
                        else
                        {
                            output = ToolOutput.DeadEnd(
                                $"You've called {call.Name} more times this run than it was going to take, and you're " +
                                $"not getting there. STOP calling {call.Name} now — work with what you've already got and " +
                                "either give your best answer or say plainly you couldn't find it. Don't call it again.");
                            
                            if (!isDeadEndWrapUp)
                            {
                                isDeadEndWrapUp = true;
                                currentMaxIterations = iteration + 3; // current + 2 more
                                var wrapUpNudge = Message.System(_input.SupervisorDeadEndNudge);
                                conversation.Add(wrapUpNudge);
                                run.Messages.Add(wrapUpNudge);
                            }
                        }
                    }
                    else
                    {
                        output = ToolOutput.DeadEnd(
                            $"You've called {call.Name} more times this run than it was going to take, and you're " +
                            $"not getting there. STOP calling {call.Name} now — work with what you've already got and " +
                            "either give your best answer or say plainly you couldn't find it. Don't call it again.");
                    }
                }
                else
                {
                    toolCallCounts[call.Name] = priorCalls + 1;
                    output = await ExecuteToolAsync(call, ct).ConfigureAwait(false);
                    if (!output.IsError)
                    {
                        successfulToolCalls.Add(signature);
                    }
                }
                // An identical RESULT is the real evidence of a loop — better evidence than an identical call.
                //
                // The repeat guard above counts calls, and only consecutive ones, because read/click/read is
                // ordinary browsing. But a shopping run spent 192 of its 250 page reads on the byte-identical
                // call, each separated by a click, and every one came back the same: the count never tripped
                // while the worker ground through its whole budget re-reading a page that had not moved. Whether
                // the world changed is a question the result answers directly, so ask it there.
                //
                // Returning the same payload again also pays for it again — a 1200-node page read is the most
                // expensive thing in the transcript, and the second copy adds nothing.
                if (!output.IsError && output.Content.Length > 0)
                {
                    if (lastOutputBySignature.TryGetValue(signature, out var previous)
                        && string.Equals(previous, output.Content, StringComparison.Ordinal))
                    {
                        unchangedResultCounts.TryGetValue(signature, out int unchanged);
                        unchangedResultCounts[signature] = unchanged + 1;

                        output = unchanged + 1 >= UnchangedResultLimit
                            ? ToolOutput.DeadEnd(
                                $"{call.Name} has now returned exactly the same result {unchanged + 1} times in a " +
                                "row. Nothing you are doing is changing it. Stop repeating this call: act on what " +
                                "you already have, try a genuinely different approach, or say plainly that you " +
                                "couldn't get there.")
                            : ToolOutput.Ok(
                                $"(Identical to the last {call.Name} with these arguments — nothing has changed " +
                                "since, so the result is not repeated here. Use the copy above; if you expected a " +
                                "change, whatever you did to cause it did not take effect.)");
                    }
                    else
                    {
                        lastOutputBySignature[signature] = output.Content;
                        unchangedResultCounts.Remove(signature); // it moved; the world is live again
                    }
                }

                // The same REFUSAL, again. Counted per tool rather than per call, because a worker trying to satisfy a
                // complaint varies its arguments on every attempt — so the identical-arguments guard never fires while
                // the identical objection repeats forever. A tool saying the same sentence three times running is not
                // going to say anything else on the fourth.
                if (output.IsError)
                {
                    if (lastRefusalByTool.TryGetValue(call.Name, out var previousRefusal)
                        && string.Equals(previousRefusal, output.Content, StringComparison.Ordinal))
                    {
                        identicalRefusalCounts.TryGetValue(call.Name, out int refused);
                        identicalRefusalCounts[call.Name] = refused + 1;

                        if (refused + 1 >= IdenticalRefusalLimit)
                            output = ToolOutput.DeadEnd(
                                $"{call.Name} has now refused {refused + 1} times with exactly the same objection:\n" +
                                output.Content +
                                "\n\nEverything you have changed between those attempts was not the thing it is " +
                                "objecting to. Re-read the objection literally and change ONLY what it names. If you " +
                                "cannot see what it means, stop and say plainly what you were trying to do and what " +
                                "it kept refusing — that is a fault worth reporting, and repeating the call is not.");
                    }
                    else
                    {
                        lastRefusalByTool[call.Name] = output.Content;
                        identicalRefusalCounts.Remove(call.Name);
                    }
                }
                else
                {
                    // It got through. Whatever the objection was, it is behind us.
                    lastRefusalByTool.Remove(call.Name);
                    identicalRefusalCounts.Remove(call.Name);
                }

                run.ToolInvocations.Add(new ToolInvocation(call.Name, call.Arguments.ToString(), output.Content, output.IsError));

                var toolMessage = Message.ToolResult(call.Id, call.Name, output.Content);
                conversation.Add(toolMessage);
                run.Messages.Add(toolMessage);

                if (output.IsError)
                {
                    toolFailures++;
                    if (output.CanRetry) anyRetryable = true; else anyTerminal = true;
                }
                else anySuccess = true;

                yield return new AgentEvent.ToolCompleted(call.Name, output.Content);
            }

            // Steer the model on failure — but only when this turn produced NOTHING useful. If anything
            // succeeded, say nothing: the model already has data and shouldn't be pushed to "keep trying"
            // (that's what made a news worker chase more sources after it already had the headlines). When
            // there's no success: out of budget → conclude with what you have; only dead ends → don't repeat
            // them, go a different way; otherwise (transient) → fix and retry.
            if (_input.NudgeOnToolError && !anySuccess && (anyTerminal || anyRetryable))
            {
                string nudgeText =
                    toolFailures >= currentMaxToolFailures
                        ? "You've hit several failures and still have no usable result. STOP calling tools now — " +
                          "answer the user with whatever you DID manage to find, or tell them plainly you couldn't " +
                          "get it. Do not invent anything."
                        : anyTerminal && !anyRetryable
                            ? "That source is a DEAD END (a hard block / not retrievable) — retrying the same call " +
                              "will never work, so don't call it again. Try a genuinely different source or " +
                              "approach; if none is likely to work, answer with what you have or say you couldn't."
                            : _input.ToolErrorNudge;
                var nudge = Message.System(nudgeText);
                conversation.Add(nudge);
                run.Messages.Add(nudge);
            }

            if (_input.EnableSupervisorGuard && iteration == currentMaxIterations - 1 && !isDeadEndWrapUp)
            {
                var decision = await RunSupervisorGuardAsync(run, ct).ConfigureAwait(false);
                if (decision == SupervisorDecision.Progress)
                {
                    currentMaxIterations += Math.Max(4, _input.MaxIterations / 2);
                    currentMaxCallsPerTool += Math.Max(2, _input.MaxCallsPerTool / 2);
                    currentMaxToolFailures += Math.Max(2, _input.MaxToolFailures / 2);

                    var turnNudge = Message.System(_input.SupervisorProgressNudge);
                    conversation.Add(turnNudge);
                    run.Messages.Add(turnNudge);
                }
                else
                {
                    isDeadEndWrapUp = true;
                    currentMaxIterations = iteration + 3; // current + 2 more

                    var wrapUpNudge = Message.System(_input.SupervisorDeadEndNudge);
                    conversation.Add(wrapUpNudge);
                    run.Messages.Add(wrapUpNudge);
                }
            }
        }

        // Iterations exhausted — emit whatever text we last produced, or a graceful message if we
        // never got a clean answer (e.g. the model kept looping).
        var lastInvocation = run.ToolInvocations.LastOrDefault();
        if (lastInvocation is not null && lastInvocation.IsError)
        {
            yield return new AgentEvent.Completed(
                $"The task could not be completed because the tool '{lastInvocation.ToolName}' failed:\n{lastInvocation.Result}");
        }
        else
        {
            var lastText = run.Messages.LastOrDefault(m => m.Role == Role.Assistant)?.Content;
            yield return new AgentEvent.Completed(
                string.IsNullOrWhiteSpace(lastText)
                    ? "I got stuck and couldn't finish that — please try rephrasing your request."
                    : lastText);
        }
    }

    /// <summary>Ask the agent a question and get its final answer text (buffered).</summary>
    public async Task<string> Answer(string userMessage, CancellationToken ct = default)
        => (await AnswerStream(userMessage, ct).ReadAllAsync(ct).ConfigureAwait(false)).Text;

    /// <summary>Ask the agent a question and get the full drained result (answer + reasoning + tools).</summary>
    public Task<AgentAnswer> AnswerAll(string userMessage, CancellationToken ct = default)
        => AnswerStream(userMessage, ct).ReadAllAsync(ct);

    // Guarantee every assistant tool call is followed by a tool result. An interrupted turn (cancellation,
    // timeout, watchdog abort) can leave an assistant tool-call group only partially answered in the persisted
    // transcript; replaying that to a provider fails with an "incomplete tool-call group" error. For each
    // assistant message, the tool results are the contiguous Role.Tool messages immediately after it; any
    // tool-call id not covered gets a synthetic failure result inserted at the end of that group. A well-formed
    // conversation is left untouched.
    private static void EnsureToolCallsAnswered(List<Message> conversation)
    {
        for (int i = 0; i < conversation.Count; i++)
        {
            if (conversation[i].Role != Role.Assistant || conversation[i].ToolCalls is not { Count: > 0 } calls)
                continue;

            var answered = new HashSet<string>(StringComparer.Ordinal);
            int end = i + 1;
            while (end < conversation.Count && conversation[end].Role == Role.Tool)
            {
                if (conversation[end].ToolCallId is { Length: > 0 } id) answered.Add(id);
                end++;
            }

            foreach (var call in calls)
            {
                if (string.IsNullOrEmpty(call.Id) || answered.Contains(call.Id)) continue;
                conversation.Insert(end++, Message.ToolResult(call.Id, call.Name,
                    "Tool execution was interrupted before it returned a result. Treat this call as failed — " +
                    "do not assume it ran; retry it or continue without it."));
            }

            i = end - 1;
        }
    }

    private async Task<ToolOutput> ExecuteToolAsync(ToolCall call, CancellationToken ct)
    {
        if (!_tools.TryGetValue(call.Name, out var tool))
            return ToolOutput.Error($"Error: no tool named '{call.Name}' is available. Available tools: " +
                                    $"{string.Join(", ", _tools.Keys)}. Use one of those.");

        try
        {
            return await tool.InvokeAsync(call.Arguments, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ToolOutput.Error($"Error executing tool '{call.Name}': {ex.Message}");
        }
    }

    /// <summary>
    /// Detect tool call(s) a model emitted as inline JSON text or XML tags
    /// where the name matches an available tool. Returns the recovered calls and the content with the
    /// tool-call text stripped out.
    /// </summary>
    private bool TryExtractInlineToolCalls(string content, out List<ToolCall> calls, out string cleaned)
    {
        calls = new List<ToolCall>();
        cleaned = content;
        if (string.IsNullOrWhiteSpace(content))
            return false;

        // Try XML first
        if (content.Contains("<tool_call>"))
        {
            if (TryExtractXmlToolCalls(content, _tools, out var xmlCalls, out var xmlCleaned))
            {
                calls = xmlCalls;
                cleaned = xmlCleaned;
                return true;
            }
        }

        // Fall back to JSON
        if (!content.Contains("\"name\""))
            return false;

        var removals = new List<(int start, int length)>();
        int index = 0;
        foreach (var (start, end, obj) in ScanJsonObjects(content))
        {
            if (!obj.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                continue;
            string name = nameEl.GetString() ?? "";
            if (!_tools.ContainsKey(name))
                continue;

            JsonElement args = default;
            if (obj.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object)
                args = a.Clone();
            else if (obj.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object)
                args = p.Clone();

            calls.Add(new ToolCall($"call_inline_{index++}", name, args));
            removals.Add((start, end - start));
        }

        if (calls.Count == 0)
            return false;

        foreach (var (start, length) in removals.OrderByDescending(r => r.start))
            cleaned = cleaned.Remove(start, length);
        cleaned = Regex.Replace(cleaned, "```[a-zA-Z]*\\s*```", "").Trim();
        return true;
    }

    private static bool TryExtractXmlToolCalls(string content, Dictionary<string, AgentTool> availableTools, out List<ToolCall> calls, out string cleaned)
    {
        calls = new List<ToolCall>();
        cleaned = content;
        
        if (string.IsNullOrWhiteSpace(content) || !content.Contains("<tool_call>"))
            return false;

        var removals = new List<(int start, int length)>();
        int index = 0;
        int searchStart = 0;
        while (true)
        {
            int startIdx = content.IndexOf("<tool_call>", searchStart);
            if (startIdx < 0) break;
            
            int endIdx = content.IndexOf("</tool_call>", startIdx);
            if (endIdx < 0)
            {
                endIdx = content.Length;
            }
            else
            {
                endIdx += "</tool_call>".Length;
            }

            int innerStart = startIdx + "<tool_call>".Length;
            int innerLength = (endIdx == content.Length) ? (content.Length - innerStart) : (endIdx - "</tool_call>".Length - innerStart);
            if (innerLength < 0) innerLength = 0;
            string inner = content.Substring(innerStart, innerLength).Trim();
            
            if (TryParseXmlToolCall(inner, availableTools, index++, out var toolCall))
            {
                calls.Add(toolCall);
                removals.Add((startIdx, endIdx - startIdx));
            }
            
            searchStart = endIdx;
            if (searchStart >= content.Length) break;
        }

        if (calls.Count == 0)
            return false;

        foreach (var (start, length) in removals.OrderByDescending(r => r.start))
        {
            cleaned = cleaned.Remove(start, length);
        }
        
        cleaned = Regex.Replace(cleaned, @"```[a-zA-Z]*\s*```", "").Trim();
        return true;
    }

    private static bool TryParseXmlToolCall(string inner, Dictionary<string, AgentTool> availableTools, int index, out ToolCall toolCall)
    {
        toolCall = default;
        
        int firstBracket = inner.IndexOf('<');
        string toolName = firstBracket >= 0 ? inner.Substring(0, firstBracket).Trim() : inner.Trim();
        
        if (string.IsNullOrEmpty(toolName) || !availableTools.ContainsKey(toolName))
            return false;

        var argsObj = new System.Text.Json.Nodes.JsonObject();
        int pos = firstBracket;
        while (pos >= 0 && pos < inner.Length)
        {
            int keyStart = inner.IndexOf("<arg_key>", pos);
            if (keyStart < 0) break;
            
            int keyEnd = inner.IndexOf("</arg_key>", keyStart);
            string keyName;
            string valStr = "";
            int nextPos = -1;

            if (keyEnd >= 0)
            {
                keyName = inner.Substring(keyStart + "<arg_key>".Length, keyEnd - (keyStart + "<arg_key>".Length)).Trim();
                
                int valStart = inner.IndexOf("<arg_value>", keyEnd);
                if (valStart >= 0)
                {
                    int valEnd = inner.IndexOf("</arg_value>", valStart);
                    if (valEnd >= 0)
                    {
                        valStr = inner.Substring(valStart + "<arg_value>".Length, valEnd - (valStart + "<arg_value>".Length));
                        nextPos = valEnd + "</arg_value>".Length;
                    }
                    else
                    {
                        valStr = inner.Substring(valStart + "<arg_value>".Length);
                        nextPos = inner.Length;
                    }
                }
                else
                {
                    nextPos = keyEnd + "</arg_key>".Length;
                }
            }
            else
            {
                int valEnd = inner.IndexOf("</arg_value>", keyStart);
                string segment;
                if (valEnd >= 0)
                {
                    segment = inner.Substring(keyStart + "<arg_key>".Length, valEnd - (keyStart + "<arg_key>".Length));
                    nextPos = valEnd + "</arg_value>".Length;
                }
                else
                {
                    segment = inner.Substring(keyStart + "<arg_key>".Length);
                    nextPos = inner.Length;
                }

                int arrowIdx = segment.IndexOf('→');
                if (arrowIdx >= 0)
                {
                    keyName = segment.Substring(0, arrowIdx).Trim();
                    valStr = segment.Substring(arrowIdx + 1);
                }
                else
                {
                    keyName = segment.Trim();
                }
            }

            if (!string.IsNullOrEmpty(keyName))
            {
                argsObj[keyName] = valStr;
            }
            
            pos = nextPos;
        }

        using var doc = JsonDocument.Parse(argsObj.ToJsonString());
        toolCall = new ToolCall($"call_xml_{index}", toolName, doc.RootElement.Clone());
        return true;
    }

    /// <summary>Yield every top-level brace-balanced JSON object found in a string (start, endExclusive, parsed).</summary>
    private static IEnumerable<(int start, int end, JsonElement obj)> ScanJsonObjects(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '{') continue;

            int depth = 0;
            bool inString = false, escaped = false;
            int matchEnd = -1;
            for (int j = i; j < s.Length; j++)
            {
                char c = s[j];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                }
                else if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) { matchEnd = j; break; }
                }
            }

            if (matchEnd < 0)
                yield break; // unbalanced — nothing more to find

            JsonElement? parsed = null;
            try
            {
                using var doc = JsonDocument.Parse(s.Substring(i, matchEnd - i + 1));
                parsed = doc.RootElement.Clone();
            }
            catch
            {
                // not valid JSON — skip past this brace and keep scanning
            }

            if (parsed is { ValueKind: JsonValueKind.Object } obj)
                yield return (i, matchEnd + 1, obj);

            i = matchEnd; // continue scanning after this object
        }
    }

    private enum SupervisorDecision
    {
        Progress,
        DeadEnd
    }

    private async Task<SupervisorDecision> RunSupervisorGuardAsync(AgentRun run, CancellationToken ct)
    {
        var toolHistory = string.Join("\n", run.ToolInvocations.Select((t, i) =>
            $"- Call {i + 1}: {t.ToolName}({t.Arguments}) - {(t.IsError ? "failed: " + t.Result : "succeeded")}"));

        if (string.IsNullOrWhiteSpace(toolHistory))
        {
            toolHistory = "(No tools called yet)";
        }

        var systemPrompt =
            "You are a supervisor monitoring an AI coding agent. The agent is trying to solve a task but is approaching its turn limit. " +
            "Analyze the tool invocation history below. Decide if the agent is stuck in an unproductive loop (e.g., repeating the same actions/commands, " +
            "repeatedly failing on the same error without fixing it, or going in circles) or if it is making genuine progress toward the goal " +
            "(e.g., solving issues, installing packages, trying new files, modifying code successfully, moving forward, or performing necessary sequential steps).\n\n" +
            "Respond with exactly one of these two words:\n" +
            "PROGRESS - if the agent is making active, genuine progress and should be allowed to continue.\n" +
            "DEAD_END - if the agent is stuck in a loop, thrashing, repeating itself, or cannot proceed.";

        var request = new ModelRequest
        {
            Model = (_input.SecondaryModel ?? _input.Model).Model,
            SystemPrompt = systemPrompt,
            Messages = new[] { Message.User($"Tool Invocation History:\n{toolHistory}\n\nIs the agent making PROGRESS or at a DEAD_END?") },
            Think = false,
            MaxOutputTokens = 100
        };

        ModelResponse? final = null;
        await foreach (var ev in _secondaryProvider.StreamAsync(request, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            if (ev is ModelStreamEvent.Completed completed)
            {
                final = completed.Response;
            }
        }

        string resultText = final?.Content ?? "";
        if (resultText.Contains("PROGRESS", StringComparison.OrdinalIgnoreCase))
        {
            return SupervisorDecision.Progress;
        }

        return SupervisorDecision.DeadEnd;
    }
}

/// <summary>A record of one tool call and its textual result.</summary>
public sealed record ToolInvocation(string ToolName, string Arguments, string Result, bool IsError = false);

/// <summary>Inspectable transcript of a single agent run.</summary>
public sealed class AgentRun
{
    public List<Message> Messages { get; } = new();

    public List<ToolInvocation> ToolInvocations { get; } = new();

    /// <summary>What this run spent, per model — every provider request made while producing it, including the
    /// cheap gate calls, so "what did that task cost?" has a real answer.</summary>
    public ModelSpend Spend { get; } = new();
}
