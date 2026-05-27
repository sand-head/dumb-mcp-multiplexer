use leptos::prelude::*;

/// Get the current allowed hosts setting.
#[server]
pub async fn get_allowed_hosts() -> Result<String, ServerFnError> {
    use crate::server::db;
    let pool = db::pool();
    let value = db::get_setting(pool, "allowed_hosts")
        .await
        .map_err(|e| ServerFnError::new(format!("DB error: {e}")))?;
    Ok(value.unwrap_or_default())
}

/// Save the allowed hosts setting and update the in-memory state.
#[server]
pub async fn save_allowed_hosts(hosts: String) -> Result<(), ServerFnError> {
    use crate::server::db;

    let pool = db::pool();
    db::set_setting(pool, "allowed_hosts", &hosts)
        .await
        .map_err(|e| ServerFnError::new(format!("DB error: {e}")))?;

    // Update the in-memory allowed hosts
    let allowed = crate::server::allowed_hosts();
    let new_hosts: Vec<String> = hosts
        .split(',')
        .map(|h| h.trim().to_string())
        .filter(|h| !h.is_empty())
        .collect();
    *allowed.write().await = new_hosts;

    Ok(())
}

#[component]
pub fn SettingsPage() -> impl IntoView {
    let hosts_resource = Resource::new(|| (), |_| get_allowed_hosts());
    let (saving, set_saving) = signal(false);
    let (error, set_error) = signal(Option::<String>::None);
    let (success, set_success) = signal(false);

    view! {
        <div>
            <div class="mb-6">
                <h2 class="text-2xl font-bold text-white">"Settings"</h2>
                <p class="text-sm text-gray-400 mt-1">"Configure global proxy settings."</p>
            </div>

            <Suspense fallback=move || view! { <div class="text-gray-400">"Loading..."</div> }>
                {move || Suspend::new(async move {
                    let current_hosts = hosts_resource.await.unwrap_or_default();
                    view! { <SettingsForm initial_hosts=current_hosts saving set_saving error set_error success set_success /> }
                })}
            </Suspense>
        </div>
    }
}

#[component]
fn SettingsForm(
    initial_hosts: String,
    saving: ReadSignal<bool>,
    set_saving: WriteSignal<bool>,
    error: ReadSignal<Option<String>>,
    set_error: WriteSignal<Option<String>>,
    success: ReadSignal<bool>,
    set_success: WriteSignal<bool>,
) -> impl IntoView {
    let (hosts, set_hosts) = signal(initial_hosts);

    let on_submit = move |ev: leptos::ev::SubmitEvent| {
        ev.prevent_default();
        set_error.set(None);
        set_success.set(false);
        set_saving.set(true);

        let hosts_val = hosts.get_untracked();

        leptos::task::spawn_local(async move {
            match save_allowed_hosts(hosts_val).await {
                Ok(()) => set_success.set(true),
                Err(e) => set_error.set(Some(e.to_string())),
            }
            set_saving.set(false);
        });
    };

    view! {
        <form on:submit=on_submit class="space-y-6">
            <div class="bg-gray-900 border border-gray-800 rounded-xl p-6">
                <h3 class="text-lg font-medium text-white mb-1">"Allowed Hosts"</h3>
                <p class="text-sm text-gray-400 mb-4">
                    "Hostnames allowed to access the MCP endpoint. This protects against DNS rebinding attacks. "
                    "Comma-separated list (e.g. "
                    <code class="text-gray-300 bg-gray-800 px-1 py-0.5 rounded text-xs">"mcps.example.com, localhost"</code>
                    ")."
                </p>
                <input
                    type="text"
                    prop:value=hosts
                    on:input=move |ev| set_hosts.set(event_target_value(&ev))
                    placeholder="localhost, 127.0.0.1, mcps.example.com"
                    class="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-white placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent font-mono text-sm"
                />
                <p class="text-xs text-gray-500 mt-2">"Leave empty to allow only localhost (default). Changes take effect immediately."</p>
            </div>

            {move || error.get().map(|e| view! {
                <div class="bg-red-900/30 border border-red-800 text-red-300 text-sm rounded-lg px-4 py-2">
                    {e}
                </div>
            })}

            {move || success.get().then(|| view! {
                <div class="bg-emerald-900/30 border border-emerald-800 text-emerald-300 text-sm rounded-lg px-4 py-2">
                    "Settings saved successfully."
                </div>
            })}

            <div class="flex justify-end">
                <button
                    type="submit"
                    disabled=saving
                    class="px-4 py-2 text-sm bg-indigo-600 hover:bg-indigo-500 disabled:opacity-50 text-white rounded-lg font-medium transition-colors"
                >
                    {move || if saving.get() { "Saving..." } else { "Save Settings" }}
                </button>
            </div>
        </form>
    }
}
