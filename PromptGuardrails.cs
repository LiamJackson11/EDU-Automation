// Helpers/PromptGuardrails.cs
// Validates OpenAI prompts before sending and validates responses after receiving.
// Prevents the AI from hallucinating assignments, fabricating grades, or generating
// content that does not match the input assignment context.

using System;
using System.Collections.Generic;
using System.Linq;
using EduAutomation.Exceptions;
using EduAutomation.Models;

namespace EduAutomation.Helpers
{
    public static class PromptGuardrails
    {
        // Maximum allowed length for an assignment description sent to OpenAI.
        private const int MaxDescriptionLength = 8000;

        // Minimum response length to consider a completion valid.
        private const int MinResponseLength = 50;

        // Phrases that indicate the AI is fabricating information it should not.
        private static readonly string[] HallucinationIndicators = new[]
        {
            "as an ai language model",
            "i cannot access",
            "i don't have access to",
            "i am unable to",
            "i cannot complete",
            "i don't have the ability",
            "as a large language model",
            "i have no information about this specific"
        };

        // Phrases that indicate the AI is refusing instead of completing the task.
        private static readonly string[] RefusalIndicators = new[]
        {
            "i'm sorry, but i cannot",
            "i cannot assist with",
            "this request goes against",
            "i'm not able to help"
        };

        // Validates the assignment before building a prompt.
        public static void ValidateAssignmentForPrompt(Assignment assignment)
        {
            if (assignment == null)
                throw new ArgumentNullException(nameof(assignment),
                    "Cannot generate AI content: assignment object is null.");

            if (string.IsNullOrWhiteSpace(assignment.Title))
                throw new AiHallucinationGuardException(
                    $"Assignment ID '{assignment.Id}' has no title. Refusing to generate content " +
                    "without a valid assignment title to prevent hallucination.");

            if (string.IsNullOrWhiteSpace(assignment.Description)
                && string.IsNullOrWhiteSpace(assignment.CourseName))
            {
                throw new AiHallucinationGuardException(
                    $"Assignment '{assignment.Title}' has no description and no course context. " +
                    "Refusing to generate content without at minimum one contextual anchor " +
                    "to prevent the AI from inventing the assignment content.");
            }
        }

        // Builds the full system + user prompt with 9th-grade guardrails applied.
        public static (string systemPrompt, string userPrompt) BuildAssignmentPrompt(
            Assignment assignment,
            List<string> supplementalData)
        {
            ValidateAssignmentForPrompt(assignment);

            string truncatedDescription = assignment.Description?.Length > MaxDescriptionLength
                ? assignment.Description[..MaxDescriptionLength] + "\n[Description truncated for length]"
                : assignment.Description ?? string.Empty;

            string systemPrompt = @"You are an academic writing assistant helping a 9th-grade high school student
complete their homework assignments. Your responses must:

1. Use clear, straightforward language appropriate for a 9th grader (approximately 14-15 years old).
2. Write at a Flesch-Kincaid Grade Level of 8 to 10 (accessible but not simplistic).
3. Avoid overly complex vocabulary, jargon, or graduate-level writing structures.
4. Only use information provided in the assignment description and the supplemental context below.
5. Never invent facts, dates, quotes, or citations that are not supplied in the context.
6. If you do not have enough information to answer a specific part of the assignment,
   write: [NEEDS MORE INFO: describe what is missing] in that section.
7. Structure the response clearly with appropriate headings, paragraphs, or lists as the
   assignment format demands.
8. Do not include any meta-commentary about being an AI, your limitations, or this prompt.
9. The response must directly address the assignment title and description provided.";

            string supplementalSection = supplementalData.Any()
                ? "\n\nSUPPLEMENTAL CONTEXT PROVIDED BY STUDENT:\n" +
                  string.Join("\n\n---\n\n", supplementalData.Select(s => s.Trim()))
                : string.Empty;

            string userPrompt = $@"COURSE: {assignment.CourseName}
ASSIGNMENT TITLE: {assignment.Title}
DUE DATE: {assignment.DueDateDisplay}

ASSIGNMENT DESCRIPTION / INSTRUCTIONS:
{truncatedDescription}
{supplementalSection}

Please complete this assignment following the instructions above.";

            return (systemPrompt, userPrompt);
        }

        // Validates the AI response to catch hallucinations or refusals.
        public static void ValidateAiResponse(string response, string assignmentTitle)
        {
            if (string.IsNullOrWhiteSpace(response))
                throw new AiHallucinationGuardException(
                    $"OpenAI returned an empty response for assignment '{assignmentTitle}'. " +
                    "This is invalid. The review item will not be created.");

            if (response.Length < MinResponseLength)
                throw new AiHallucinationGuardException(
                    $"OpenAI response for '{assignmentTitle}' is suspiciously short ({response.Length} chars). " +
                    "Minimum acceptable length is {MinResponseLength} characters. Rejecting response.");

            string lowerResponse = response.ToLowerInvariant();

            foreach (string indicator in HallucinationIndicators)
            {
                if (lowerResponse.Contains(indicator))
                    throw new AiHallucinationGuardException(
                        $"OpenAI response for '{assignmentTitle}' contains a hallucination indicator: " +
                        $"'{indicator}'. The response was blocked to prevent submitting invalid content. " +
                        "Please add more context in the Data Dump and regenerate.");
            }

            foreach (string refusal in RefusalIndicators)
            {
                if (lowerResponse.Contains(refusal))
                    throw new AiHallucinationGuardException(
                        $"OpenAI refused to complete assignment '{assignmentTitle}'. " +
                        $"Refusal phrase detected: '{refusal}'. Please review the assignment description.");
            }
        }
    }
}
