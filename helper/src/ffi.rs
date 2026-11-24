// The same conversation as ump-helper's, for a program it's linked into: the arguments as a JSON array, each line it says through
// a callback, each answer through ump_answer. One run at a time.

use std::{
    ffi::{CStr, CString, c_char},
    panic::{AssertUnwindSafe, catch_unwind},
    sync::{
        Arc, Mutex,
        mpsc::{Receiver, Sender, channel},
    },
};

use rootcause::prelude::*;
use serde_json::json;
use tokio::sync::Notify;

use crate::{HOST, Host, emit, run};

// Called from any thread; the line is only there during the call.
pub type Say = extern "C" fn(*const c_char);

struct App {
    say: Say,
    answers: Mutex<Receiver<String>>,
}

impl Host for App {
    fn say(&self, line: &str) {
        if let Ok(line) = CString::new(line) {
            (self.say)(line.as_ptr());
        }
    }

    // (the sender goes when the run is cancelled, which ends the wait)
    fn answer(&self) -> Result<String, Report> {
        match self.answers.lock().unwrap_or_else(|poisoned| poisoned.into_inner()).recv() {
            Ok(line) => Ok(line),
            Err(_) => bail!("No answer: cancelled"),
        }
    }
}

static BUSY: Mutex<()> = Mutex::new(());
static ANSWERS: Mutex<Option<Sender<String>>> = Mutex::new(None);
// The run that's going. A cancel that comes before it's listening waits for it, and none is left over for the next one.
static CANCEL: Mutex<Option<Arc<Notify>>> = Mutex::new(None);

fn text(line: *const c_char) -> Option<String> {
    if line.is_null() {
        return None;
    }
    // Safe by contract: a C string that stays there for the call.
    unsafe { CStr::from_ptr(line) }.to_str().ok().map(str::to_string)
}

fn set_cancel(cancel: Option<Arc<Notify>>) {
    *CANCEL.lock().unwrap_or_else(|poisoned| poisoned.into_inner()) = cancel;
}

fn set_answers(sender: Option<Sender<String>>) {
    *ANSWERS.lock().unwrap_or_else(|poisoned| poisoned.into_inner()) = sender;
}

// Blocks until it's over, so never on the app's main thread. 0 done, 1 failed (the "error" event says why), 2 already running.
#[unsafe(no_mangle)]
pub extern "C" fn ump_run(args: *const c_char, say: Say) -> i32 {
    let Ok(_alone) = BUSY.try_lock() else { return 2 };
    let (sender, receiver) = channel();
    set_answers(Some(sender));
    let cancelled = Arc::new(Notify::new());
    set_cancel(Some(cancelled.clone()));
    *HOST.write().unwrap_or_else(|poisoned| poisoned.into_inner()) = Some(Arc::new(App { say, answers: Mutex::new(receiver) }));

    // Nothing unwinds into the app: a bug in here is a failed run.
    let done = catch_unwind(AssertUnwindSafe(|| -> Result<(), Report> {
        let args: Vec<String> = serde_json::from_str(&text(args).unwrap_or_default())?;
        let runtime = tokio::runtime::Builder::new_multi_thread().worker_threads(2).enable_all().build()?;
        let over = runtime.block_on(async {
            tokio::select! {
                over = run(&args) => over,
                _ = cancelled.notified() => Err(report!("Cancelled").into()),
            }
        });
        // (what's still going on a thread of its own, a name being looked up, isn't waited for)
        runtime.shutdown_background();
        over
    }));
    let code = match done {
        Ok(Ok(())) => 0,
        Ok(Err(e)) => {
            emit(json!({ "event": "error", "message": e.to_string() }));
            1
        }
        Err(_) => {
            emit(json!({ "event": "error", "message": "Something broke inside" }));
            1
        }
    };
    set_cancel(None);
    set_answers(None);
    *HOST.write().unwrap_or_else(|poisoned| poisoned.into_inner()) = None;
    code
}

// The answer to an "ask" event.
#[unsafe(no_mangle)]
pub extern "C" fn ump_answer(line: *const c_char) {
    if let (Some(line), Some(sender)) = (text(line), ANSWERS.lock().unwrap_or_else(|poisoned| poisoned.into_inner()).as_ref()) {
        let _ = sender.send(line);
    }
}

// Ends the run that's going, if there is one.
#[unsafe(no_mangle)]
pub extern "C" fn ump_cancel() {
    if let Some(run) = CANCEL.lock().unwrap_or_else(|poisoned| poisoned.into_inner()).as_ref() {
        set_answers(None);
        run.notify_one();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    static SAID: Mutex<Vec<String>> = Mutex::new(Vec::new());

    extern "C" fn say(line: *const c_char) {
        SAID.lock().unwrap().push(text(line).unwrap());
    }

    fn go(args: &[&str]) -> (i32, String) {
        SAID.lock().unwrap().clear();
        let args = CString::new(serde_json::to_string(args).unwrap()).unwrap();
        let code = ump_run(args.as_ptr(), say);
        (code, SAID.lock().unwrap().join("\n"))
    }

    // One test, since one run at a time is the rule.
    #[test]
    fn the_app_s_conversation() {
        let data = std::env::temp_dir().join(format!("umprenew-test-{}", std::process::id()));
        let data = data.to_str().unwrap();

        let (code, said) = go(&["--data", data, "status"]);
        assert!(code == 0 && said.contains(r#""event":"account""#), "{said}");
        let (code, said) = go(&["--data", data, "renew-here", "a.b", "127.0.0.1"]);
        assert!(code == 1 && said.contains("Not paired"), "{said}");
        let (code, said) = go(&["nonsense"]);
        assert!(code == 1 && said.contains("usage"), "{said}");

        // Cancelled while it waits for the password: it ends, and the next run is as good as the first.
        let waiting = std::thread::spawn({
            let data = data.to_string();
            move || go(&["--data", &data, "login", "a@example.com"])
        });
        while !SAID.lock().unwrap().iter().any(|l| l.contains("password")) {
            std::thread::sleep(std::time::Duration::from_millis(20));
        }
        assert_eq!(ump_run(c"[]".as_ptr(), say), 2);
        ump_cancel();
        assert_eq!(waiting.join().unwrap().0, 1);
        assert_eq!(go(&["--data", data, "status"]).0, 0);
        // With nothing going there's nothing to cancel, and the next run doesn't end because of it.
        ump_cancel();
        assert_eq!(go(&["--data", data, "status"]).0, 0);
        let _ = std::fs::remove_dir_all(data);
    }
}
