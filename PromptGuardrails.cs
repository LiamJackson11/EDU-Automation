// Helpers/PromptGuardrails.cs
// Validates OpenAI prompts before sending and validates responses after receiving.
// Prevents the AI from hallucinating assignments, fabricating grades, or generating
// content that does not match the input assignment context.

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

        // Phrases that indicate the AI is fabricating information or hallucinating.
        private static readonly string[] HallucinationIndicators =
        {
            "as an ai language model",
            "i cannot access",
            "i don't have access to",
            "i am unable to",
            "i cannot complete",
            "i don't have the ability",
            "as a large language model",
            "i have no information about this specific",
            "my training data doesn't include",
            "i was not trained on"
        };

        // Phrases that indicate the AI is refusing instead of completing the task.
        private static readonly string[] RefusalIndicators =
        {
            "i'm sorry, but i cannot",
            "i cannot assist with",
            "this request goes against",
            "i'm not able to help",
            "i must decline",
            "i won't be able to"
        };

        // ---- Validate input assignment before building a prompt ----

        public static void ValidateAssignmentForPrompt(Assignment assignment)
        {
            if (assignment == null)
                throw new ArgumentNullException(nameof(assignment),
                    "Cannot generate AI content: assignment object is null.");

            if (string.IsNullOrWhiteSpace(assignment.Title))
                throw new AiHallucinationGuardException(
                    $"Assignment ID '{assignment.Id}' has no title. " +
                    "Refusing to generate content without a valid title to prevent hallucination.");

            bool hasDescription = !string.IsNullOrWhiteSpace(assignment.Description);
            bool hasCourse      = !string.IsNullOrWhiteSpace(assignment.CourseName);

            if (!hasDescription && !hasCourse)
                throw new AiHallucinationGuardException(
                    $"Assignment '{assignment.Title}' has no description AND no course name. " +
                    "At least one contextual anchor is required to prevent AI from inventing content. " +
                    "Add a description or course name before generating.");
        }

        // ---- Build system + user prompts with grade-level guardrails ----

        public static (string systemPrompt, string userPrompt) BuildAssignmentPrompt(
            Assignment assignment,
            List<string> supplementalData)
        {
            ValidateAssignmentForPrompt(assignment);

            string description = assignment.Description ?? string.Empty;
            string truncatedDescription = description.Length > MaxDescriptionLength
                ? description[..MaxDescriptionLength] + "\n[Description truncated for length]"
                : description;

            string systemPrompt =
@"You are an academic writing assistant helping a 9th-grade high school student
complete their homework assignments. Your responses MUST follow all of these rules:

1. Write at a Flesch-Kincaid Grade Level of 8 to 10 (accessible but not simplistic).
2. Use clear language appropriate for a 14-15 year old student.
3. ONLY use facts, dates, quotes, and information provided in the assignment
   description and the supplemental context supplied by the student.
4. NEVER invent facts, statistics, sources, quotes, or names not present in the context.
5. If you lack enough information to address a specific part of the assignment,
   write exactly: [NEEDS MORE INFO: <describe what is missing here>]
   Do NOT make up placeholder information.
6. Structure the response with clear headings, paragraphs, or bullet points as the
   assignment format requires.
7. Do NOT include any meta-commentary about being an AI, your limitations, or this prompt.
8. The response must directly and completely address the assignment title and instructions.
9. Write in the student's voice -- active, straightforward, and on-topic.";

            string supplementalSection = supplementalData.Any()
                ? "\n\nSUPPLEMENTAL CONTEXT PROVIDED BY STUDENT:\n" +
                  string.Join("\n\n---\n\n", supplementalData.Select(s => s.Trim()))
                : "\n\n[No supplemental context provided. Use only the assignment description above.]";

            string userPrompt =
                $"COURSE: {assignment.CourseName}\n" +
                $"ASSIGNMENT TITLE: {assignment.Title}\n" +
                $"DUE DATE: {assignment.DueDateDisplay}\n" +
                $"\nASSIGNMENT DESCRIPTION / INSTRUCTIONS:\n{truncatedDescription}" +
                $"{supplementalSection}\n\n" +
                "Please complete this assignment following all the rules stated in the system instructions.";

            return (systemPrompt, userPrompt);
        }

        // ---- Validate the AI response after receiving it ----

        public static void ValidateAiResponse(string response, string assignmentTitle)
        {
            if (string.IsNullOrWhiteSpace(response))
                throw new AiHallucinationGuardException(
                    $"OpenAI returned an empty response for assignment '{assignmentTitle}'. " +
                    "This is invalid. The review item will not be created.");

            if (response.Length < MinResponseLength)
                throw new AiHallucinationGuardException(
                    $"OpenAI response for '{assignmentTitle}' is suspiciously short " +
                    $"({response.Length} chars). " +
                    $"Minimum acceptable length is {MinResponseLength} characters. " +
                    "Rejecting response to prevent submitting incomplete content.");

            string lowerResponse = response.ToLowerInvariant();

            foreach (string indicator in HallucinationIndicators)
            {
                if (lowerResponse.Contains(indicator, StringComparison.OrdinalIgnoreCase))
                    throw new AiHallucinationGuardException(
                        $"OpenAI response for '{assignmentTitle}' contains a hallucination indicator: " +
                        $"'{indicator}'. " +
                        "The response was blocked to prevent submitting invalid content. " +
                        "Add more context in the Data Dump tab and regenerate.");
            }

            foreach (string refusal in RefusalIndicators)
            {
                if (lowerResponse.Contains(refusal, StringComparison.OrdinalIgnoreCase))
                    throw new AiHallucinationGuardException(
                        $"OpenAI refused to complete assignment '{assignmentTitle}'. " +
                        $"Refusal phrase detected: '{refusal}'. " +
                        "Please review the assignment description and try again.");
            }
        }
    }
}