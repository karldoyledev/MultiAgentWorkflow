document.querySelectorAll("form[data-lesson-check]").forEach((form) => {
  form.addEventListener("submit", (event) => {
    event.preventDefault();

    const selection = form.querySelector("input[name='answer']:checked");
    const feedback = form.querySelector("[data-feedback]");
    const acceptedAnswers = (form.dataset.answers ?? "")
      .split("|")
      .map((answer) => answer.trim().toLowerCase());
    const answer = selection?.value.trim().toLowerCase() ?? "";
    const isCorrect = acceptedAnswers.includes(answer);

    feedback.dataset.state = isCorrect ? "correct" : "incorrect";
    feedback.textContent = isCorrect
      ? form.dataset.correctFeedback
      : form.dataset.incorrectFeedback;
  });
});