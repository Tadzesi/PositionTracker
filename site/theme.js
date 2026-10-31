try{var t=localStorage.getItem('pt-site-theme');if(t==='dark'||t==='light')document.documentElement.setAttribute('data-theme',t)}catch(e){}

/* Language: one preference for the whole origin ("pt-lang", shared with the app under /tracker/app/). The explicit choice wins;
   without one the browser language decides. Only the Slovak root /tracker/ redirects, a direct link to /tracker/en/ is respected. */
try{var d=document.documentElement,l=localStorage.getItem('pt-lang');if(l!=='sk'&&l!=='en')l=/^sk\b/i.test(navigator.language||'')?'sk':'en';if(d.lang==='sk'&&l==='en'&&/\/$/.test(location.pathname)&&!/\/en\/$/.test(location.pathname))location.replace(location.pathname+'en/'+location.search+location.hash)}catch(e){}
