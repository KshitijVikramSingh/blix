// The page's whole job is getting these commands into a terminal, so copying
// strips the prompt and joins wrapped continuation lines back together.
function commandsIn(pre) {
  return [...pre.querySelectorAll('.p')]
    .map((prompt) => {
      let text = '';
      for (let node = prompt.nextSibling; node; node = node.nextSibling) {
        if (node.nodeType === 1 && node.classList.contains('p')) break;
        text += node.textContent;
      }
      return text.replace(/\\\n\s*/g, '').trim();
    })
    .filter(Boolean)
    .join('\n');
}

async function write(text) {
  if (navigator.clipboard && window.isSecureContext) {
    await navigator.clipboard.writeText(text);
    return;
  }

  const field = document.createElement('textarea');
  field.value = text;
  field.setAttribute('readonly', '');
  field.style.cssText = 'position:fixed;opacity:0';
  document.body.append(field);
  field.select();
  document.execCommand('copy');
  field.remove();
}

for (const button of document.querySelectorAll('.copy')) {
  const label = button.getAttribute('aria-label');

  button.addEventListener('click', async () => {
    const pre = document.getElementById(button.dataset.copy);
    if (!pre) return;

    try {
      await write(commandsIn(pre) || pre.innerText);
    } catch {
      return;                       // leave the button as it was; nothing was copied
    }

    // The icon is the only feedback, so the label has to carry it for anyone
    // who cannot see the swap.
    button.dataset.copied = '';
    button.setAttribute('aria-label', 'Copied');

    setTimeout(() => {
      delete button.dataset.copied;
      button.setAttribute('aria-label', label);
    }, 1800);
  });
}
