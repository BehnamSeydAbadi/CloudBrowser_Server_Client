using System;

namespace BrowserServer
{
    public static class JavascriptFunctions
    {
        public static string script =
                @"(function ()
                    {

                        var json = {};
                        var isText = false;
                        var activeElement = document.activeElement;
                        if (activeElement) {
                            if (activeElement.tagName.toLowerCase() === 'textarea') {
                                isText = true;
                            } else {
                                if (activeElement.tagName.toLowerCase() === 'input') {
                                    if (activeElement.hasAttribute('type')) {
                                        var inputType = activeElement.getAttribute('type').toLowerCase();
                                        if (inputType === 'text' || inputType === 'email' || inputType === 'password' || inputType === 'tel' || inputType === 'number' || inputType === 'range' || inputType === 'search' || inputType === 'url') {
                                            isText = true;
                                        }
                                    }
                                }
                            }
                        }
                        if(isText){

                        }

json.isText = isText;
json.text = document.activeElement.value;

                        //return isText;
return JSON.stringify(json);
                    })();";

        public static string GetActiveElementText = @"(

function ()
{
return document.activeElement.value;
}
)();
";

        public static string GetFocusActiveElementText = @"(

function ()
{
document.activeElement.focus();
return document.activeElement.value;
}
)();
";

        /// <summary>
        /// Insert text into the focused field. Uses the native value setter so React-controlled inputs update.
        /// Pass already JSON-serialized string literal for <paramref name="jsonTextLiteral"/> (including quotes).
        /// </summary>
        public static string InsertText(string jsonTextLiteral)
        {
            return @"
(function(){
  var text = " + jsonTextLiteral + @";
  var el = document.activeElement;
  if (!el) return false;
  if (el.isContentEditable) {
    document.execCommand('insertText', false, text);
    return true;
  }
  var tag = (el.tagName || '').toLowerCase();
  if (tag !== 'input' && tag !== 'textarea') return false;
  var start = (typeof el.selectionStart === 'number') ? el.selectionStart : (el.value || '').length;
  var end = (typeof el.selectionEnd === 'number') ? el.selectionEnd : start;
  var value = el.value || '';
  var next = value.slice(0, start) + text + value.slice(end);
  var proto = tag === 'textarea' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype;
  var desc = Object.getOwnPropertyDescriptor(proto, 'value');
  if (desc && desc.set) desc.set.call(el, next); else el.value = next;
  try { el.selectionStart = el.selectionEnd = start + text.length; } catch (e) {}
  try { el.dispatchEvent(new InputEvent('input', { bubbles: true, cancelable: true, inputType: 'insertText', data: text })); }
  catch (e1) { el.dispatchEvent(new Event('input', { bubbles: true })); }
  return true;
})();";
        }

        public static string Backspace =
@"
(function(){
  var el = document.activeElement;
  if (!el) return false;
  if (el.isContentEditable) {
    document.execCommand('delete', false, null);
    return true;
  }
  var tag = (el.tagName || '').toLowerCase();
  if (tag !== 'input' && tag !== 'textarea') return false;
  var start = (typeof el.selectionStart === 'number') ? el.selectionStart : (el.value || '').length;
  var end = (typeof el.selectionEnd === 'number') ? el.selectionEnd : start;
  var value = el.value || '';
  if (start === end) {
    if (start <= 0) return false;
    start = start - 1;
  }
  var next = value.slice(0, start) + value.slice(end);
  var proto = tag === 'textarea' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype;
  var desc = Object.getOwnPropertyDescriptor(proto, 'value');
  if (desc && desc.set) desc.set.call(el, next); else el.value = next;
  try { el.selectionStart = el.selectionEnd = start; } catch (e) {}
  try { el.dispatchEvent(new InputEvent('input', { bubbles: true, cancelable: true, inputType: 'deleteContentBackward', data: null })); }
  catch (e1) { el.dispatchEvent(new Event('input', { bubbles: true })); }
  return true;
})();";
    }
}
