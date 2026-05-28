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

#[derive(Clone, Debug)]
struct HostEntry {
    id: u32,
    value: RwSignal<String>,
}

#[component]
pub fn SettingsPage() -> impl IntoView {
    let hosts_resource = Resource::new(|| (), |_| get_allowed_hosts());

    view! {
        <div>
            <div class="mb-6">
                <h2 class="text-2xl font-bold text-white">"Settings"</h2>
                <p class="text-sm text-gray-400 mt-1">"Configure global proxy settings."</p>
            </div>

            <Suspense fallback=move || view! { <div class="text-gray-400">"Loading..."</div> }>
                {move || Suspend::new(async move {
                    let current_hosts = hosts_resource.await.unwrap_or_default();
                    view! { <SettingsForm initial_hosts=current_hosts /> }
                })}
            </Suspense>
        </div>
    }
}

#[component]
fn SettingsForm(initial_hosts: String) -> impl IntoView {
    let initial_entries: Vec<HostEntry> = initial_hosts
        .split(',')
        .map(|h| h.trim().to_string())
        .filter(|h| !h.is_empty())
        .enumerate()
        .map(|(i, h)| HostEntry {
            id: i as u32,
            value: RwSignal::new(h),
        })
        .collect();

    let (next_id, set_next_id) = signal(initial_entries.len() as u32);
    let (entries, set_entries) = signal(initial_entries);
    let (saving, set_saving) = signal(false);
    let (error, set_error) = signal(Option::<String>::None);
    let (success, set_success) = signal(false);

    let add_entry = move |_: leptos::ev::MouseEvent| {
        let id = next_id.get_untracked();
        set_next_id.set(id + 1);
        set_entries.update(|e| {
            e.push(HostEntry {
                id,
                value: RwSignal::new(String::new()),
            });
        });
    };

    let remove_entry = move |id: u32| {
        set_entries.update(|e| e.retain(|entry| entry.id != id));
    };

    let on_submit = move |ev: leptos::ev::SubmitEvent| {
        ev.prevent_default();
        set_error.set(None);
        set_success.set(false);
        set_saving.set(true);

        let hosts_val = entries
            .get_untracked()
            .iter()
            .map(|e| e.value.get_untracked())
            .filter(|h| !h.is_empty())
            .collect::<Vec<_>>()
            .join(",");

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
                <div class="flex items-center justify-between mb-1">
                    <h3 class="text-lg font-medium text-white">"Allowed Hosts"</h3>
                    <button
                        type="button"
                        on:click=add_entry
                        class="text-xs text-indigo-400 hover:text-indigo-300 transition-colors"
                    >
                        "+ Add Host"
                    </button>
                </div>
                <p class="text-sm text-gray-400 mb-4">
                    "Hostnames permitted to reach the MCP endpoint, guarding against DNS rebinding attacks. "
                    "Loopback addresses are always allowed."
                </p>
                <div class="space-y-2">
                    {move || {
                        entries.get().into_iter().map(|entry| {
                            let entry_id = entry.id;
                            view! {
                                <div class="flex gap-2 items-center">
                                    <input
                                        type="text"
                                        prop:value=entry.value
                                        on:input=move |ev| entry.value.set(event_target_value(&ev))
                                        placeholder="mcps.example.com"
                                        class="flex-1 bg-gray-800 border border-gray-700 rounded-lg px-3 py-1.5 text-white text-sm font-mono placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
                                    />
                                    <button
                                        type="button"
                                        on:click=move |_| remove_entry(entry_id)
                                        class="text-gray-500 hover:text-red-400 transition-colors px-1"
                                    >
                                        "✕"
                                    </button>
                                </div>
                            }
                        }).collect::<Vec<_>>()
                    }}
                </div>
                {move || entries.get().is_empty().then(|| view! {
                    <p class="text-sm text-gray-600 italic">"No additional hosts configured — only loopback is allowed."</p>
                })}
            </div>

            {move || error.get().map(|e| view! {
                <div class="bg-red-900/30 border border-red-800 text-red-300 text-sm rounded-lg px-4 py-2">
                    {e}
                </div>
            })}

            {move || success.get().then(|| view! {
                <div class="bg-emerald-900/30 border border-emerald-800 text-emerald-300 text-sm rounded-lg px-4 py-2">
                    "Settings saved."
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
