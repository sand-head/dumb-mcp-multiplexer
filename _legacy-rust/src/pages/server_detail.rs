use leptos::prelude::*;
use leptos_router::hooks::use_params_map;

use crate::components::server_form::ServerForm;
use crate::types::McpServer;

/// Fetch a single server by slug.
#[server]
pub async fn get_server(slug: String) -> Result<Option<McpServer>, ServerFnError> {
    use crate::server::db;
    let pool = db::pool();
    db::get_server_by_slug(pool, &slug)
        .await
        .map_err(|e| ServerFnError::new(format!("DB error: {e}")))
}

/// Delete a server by id.
#[server]
pub async fn delete_server_action(id: String) -> Result<(), ServerFnError> {
    use crate::server::db;
    let pool = db::pool();
    db::delete_server(pool, &id)
        .await
        .map_err(|e| ServerFnError::new(format!("DB error: {e}")))
}

/// Toggle server enabled state.
#[server]
pub async fn toggle_server(id: String) -> Result<bool, ServerFnError> {
    use crate::server::db;
    let pool = db::pool();
    db::toggle_server_enabled(pool, &id)
        .await
        .map_err(|e| ServerFnError::new(format!("DB error: {e}")))
}

#[component]
pub fn ServerDetailPage() -> impl IntoView {
    let params = use_params_map();
    let slug = move || params.read().get("slug").unwrap_or_default();

    let server_resource = Resource::new(move || slug(), |slug| get_server(slug));

    view! {
        <Suspense fallback=move || view! { <div class="text-gray-400">"Loading..."</div> }>
            {move || Suspend::new(async move {
                match server_resource.await {
                    Ok(Some(server)) => {
                        view! { <ServerDetailView server=server resource=server_resource/> }.into_any()
                    }
                    Ok(None) => {
                        view! {
                            <div class="text-center py-12">
                                <h2 class="text-xl text-gray-300">"Server not found"</h2>
                                <a href="/" class="text-indigo-400 hover:text-indigo-300 text-sm mt-2 inline-block">
                                    "← Back to dashboard"
                                </a>
                            </div>
                        }.into_any()
                    }
                    Err(e) => {
                        view! {
                            <div class="text-red-400">{format!("Error: {e}")}</div>
                        }.into_any()
                    }
                }
            })}
        </Suspense>
    }
}

#[component]
fn ServerDetailView(
    server: McpServer,
    resource: Resource<Result<Option<McpServer>, ServerFnError>>,
) -> impl IntoView {
    let server_id = server.id.clone();
    let server_id_toggle = server.id.clone();
    let server_for_edit = server.clone();
    let (enabled, set_enabled) = signal(server.enabled);
    let (show_edit, set_show_edit) = signal(false);
    let (show_delete_confirm, set_show_delete_confirm) = signal(false);

    let on_toggle = move |_| {
        let id = server_id_toggle.clone();
        leptos::task::spawn_local(async move {
            if let Ok(new_state) = toggle_server(id).await {
                set_enabled.set(new_state);
            }
        });
    };

    let on_delete = move |_| {
        let id = server_id.clone();
        leptos::task::spawn_local(async move {
            if delete_server_action(id).await.is_ok() {
                let navigate = leptos_router::hooks::use_navigate();
                navigate("/", Default::default());
            }
        });
    };

    view! {
        <div>
            <ServerDetailHeader
                name=server.name.clone()
                slug=server.slug.clone()
                enabled=enabled
                on_toggle=on_toggle
                on_edit=move |_| set_show_edit.set(true)
                on_delete=move |_| set_show_delete_confirm.set(true)
            />

            <ServerConfigPanel
                transport=server.transport.to_string()
                enabled=enabled
                url=server.url.clone()
                header_count=server.headers.len()
                slug=server.slug.clone()
            />

            // Edit modal
            {move || {
                if show_edit.get() {
                    let s = server_for_edit.clone();
                    view! {
                        <ServerForm
                            server=s
                            on_saved=move |_| {
                                set_show_edit.set(false);
                                resource.refetch();
                            }
                            on_cancel=move |_| set_show_edit.set(false)
                        />
                    }.into_any()
                } else {
                    ().into_any()
                }
            }}

            // Delete confirmation
            {move || {
                if show_delete_confirm.get() {
                    view! {
                        <DeleteConfirmModal
                            on_confirm=on_delete.clone()
                            on_cancel=move |_| set_show_delete_confirm.set(false)
                        />
                    }.into_any()
                } else {
                    ().into_any()
                }
            }}
        </div>
    }
}

#[component]
fn ServerDetailHeader(
    name: String,
    slug: String,
    enabled: ReadSignal<bool>,
    on_toggle: impl Fn(leptos::ev::MouseEvent) + 'static,
    on_edit: impl Fn(leptos::ev::MouseEvent) + 'static,
    on_delete: impl Fn(leptos::ev::MouseEvent) + 'static,
) -> impl IntoView {
    view! {
        <div class="flex items-center justify-between mb-8">
            <div class="flex items-center gap-4">
                <a href="/" class="text-gray-400 hover:text-white transition-colors">"← Back"</a>
                <div>
                    <h2 class="text-2xl font-bold text-white">{name}</h2>
                    <p class="text-sm text-gray-500 font-mono">{slug}</p>
                </div>
            </div>
            <div class="flex items-center gap-3">
                <button
                    on:click=on_toggle
                    class="px-3 py-1.5 text-sm rounded-lg border transition-colors"
                    class:border-emerald-700=enabled
                    class:text-emerald-400=enabled
                    class:border-gray-700=move || !enabled.get()
                    class:text-gray-400=move || !enabled.get()
                >
                    {move || if enabled.get() { "Enabled" } else { "Disabled" }}
                </button>
                <button
                    on:click=on_edit
                    class="px-3 py-1.5 text-sm bg-indigo-600 hover:bg-indigo-500 text-white rounded-lg transition-colors"
                >
                    "Edit"
                </button>
                <button
                    on:click=on_delete
                    class="px-3 py-1.5 text-sm border border-red-800 text-red-400 hover:bg-red-900/30 rounded-lg transition-colors"
                >
                    "Delete"
                </button>
            </div>
        </div>
    }
}

#[component]
fn ServerConfigPanel(
    transport: String,
    enabled: ReadSignal<bool>,
    url: Option<String>,
    header_count: usize,
    slug: String,
) -> impl IntoView {
    view! {
        <div class="bg-gray-900 border border-gray-800 rounded-xl p-6">
            <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                    <dt class="text-xs font-medium text-gray-500 uppercase tracking-wider">"Transport"</dt>
                    <dd class="mt-1 text-sm text-gray-200">{transport}</dd>
                </div>
                <div>
                    <dt class="text-xs font-medium text-gray-500 uppercase tracking-wider">"Status"</dt>
                    <dd class="mt-1 text-sm">
                        <span class=move || if enabled.get() { "text-emerald-400" } else { "text-gray-500" }>
                            {move || if enabled.get() { "● Enabled" } else { "○ Disabled" }}
                        </span>
                    </dd>
                </div>
                <div class="md:col-span-2">
                    <dt class="text-xs font-medium text-gray-500 uppercase tracking-wider">"URL"</dt>
                    <dd class="mt-1 text-sm text-gray-200 font-mono">
                        {url.unwrap_or_else(|| "Not configured".to_string())}
                    </dd>
                </div>
                <div class="md:col-span-2">
                    <dt class="text-xs font-medium text-gray-500 uppercase tracking-wider">"Custom Headers"</dt>
                    <dd class="mt-1 text-sm text-gray-200">
                        {if header_count > 0 { format!("{header_count} header(s) configured") } else { "None".to_string() }}
                    </dd>
                </div>
            </div>
        </div>

        <div class="mt-6 bg-gray-900/50 border border-gray-800 rounded-xl p-5">
            <h3 class="text-sm font-medium text-gray-300 mb-2">"Namespace Prefix"</h3>
            <p class="text-sm text-gray-400 mb-3">
                "Tools from this server are exposed with the prefix:"
            </p>
            <code class="text-indigo-400 bg-gray-800 px-3 py-1.5 rounded-lg text-sm">
                {format!("{}__<tool_name>", slug)}
            </code>
        </div>
    }
}

#[component]
fn DeleteConfirmModal(
    on_confirm: impl Fn(leptos::ev::MouseEvent) + 'static,
    on_cancel: impl Fn(leptos::ev::MouseEvent) + 'static,
) -> impl IntoView {
    view! {
        <div class="fixed inset-0 bg-black/60 backdrop-blur-sm flex items-center justify-center z-50 p-4">
            <div class="bg-gray-900 border border-gray-700 rounded-2xl w-full max-w-sm shadow-2xl p-6">
                <h3 class="text-lg font-semibold text-white mb-2">"Delete Server?"</h3>
                <p class="text-sm text-gray-400 mb-5">
                    "This will permanently remove the server and all its cached capabilities. This cannot be undone."
                </p>
                <div class="flex justify-end gap-3">
                    <button
                        on:click=on_cancel
                        class="px-4 py-2 text-sm text-gray-300 hover:text-white transition-colors"
                    >
                        "Cancel"
                    </button>
                    <button
                        on:click=on_confirm
                        class="px-4 py-2 text-sm bg-red-600 hover:bg-red-500 text-white rounded-lg font-medium transition-colors"
                    >
                        "Delete"
                    </button>
                </div>
            </div>
        </div>
    }
}
