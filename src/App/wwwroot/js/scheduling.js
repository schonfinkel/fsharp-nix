(() => {
  const setEffectiveInstant = (form) => {
    const localInput = form.querySelector("[data-effective-local]");
    const utcInput = form.querySelector("[data-effective-utc]");

    if (!localInput || !utcInput) {
      return;
    }

    if (!localInput.value) {
      utcInput.value = "";
      return;
    }

    const localTime = new Date(localInput.value);
    utcInput.value = Number.isNaN(localTime.getTime()) ? "" : localTime.toISOString();
  };

  document.addEventListener(
    "submit",
    (event) => {
      const form = event.target;

      if (form instanceof HTMLFormElement && form.matches("[data-feature-schedule]")) {
        setEffectiveInstant(form);
      }
    },
    true,
  );
})();
