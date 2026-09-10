(() => {
  const renderQrCode = () => {
    const target = document.getElementById("qr-code");

    if (target && window.QRCode) {
      target.replaceChildren();
      new window.QRCode(target, {
        text: target.dataset.url,
        width: 192,
        height: 192,
      });
    }
  };

  document.addEventListener("DOMContentLoaded", renderQrCode);
  document.addEventListener("htmx:afterSwap", renderQrCode);
})();
